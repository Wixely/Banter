using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Banter.Server.Tools;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>
/// Server-side tool execution (PLAN §8). The rules that matter are all refusals: a client must
/// not be able to call a tool, an agent must not be able to call one it was not granted, and
/// only an admin may change who holds what. A real MCP upstream would prove none of that, so
/// the backend here is a fake and the server's authorization is the thing under test.
/// </summary>
public sealed class AgentToolTests : IAsyncLifetime
{
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("admin", "pw", isAgent: false, isAdmin: true)
        .AddUser("human", "pw")
        .AddUser("dagger", "pw", isAgent: true)
        .AddUser("scout", "pw", isAgent: true);

    private readonly FakeToolBroker _tools = new();
    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    /// <summary>
    /// A tool backend with no upstreams: grants live in a dictionary and a call just records
    /// itself. Everything the server decides happens before this is reached.
    /// </summary>
    private sealed class FakeToolBroker : IToolBroker
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _grants = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Agent, string Tool)> Calls { get; } = [];

        /// <summary>Set to hold a call open, so "does this block the engine?" is answerable.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public IReadOnlyList<ToolDescriptorPayload> AllTools() =>
        [
            new("gh_list_issues", "List issues", "{}", "github"),
            new("read_file", "Read a file", "{}", "fs"),
        ];

        public Task<IReadOnlyList<ToolDescriptorPayload>> ToolsForAsync(
            string agent, CancellationToken cancellationToken = default)
        {
            var granted = _grants.TryGetValue(agent, out var g) ? g : [];
            return Task.FromResult<IReadOnlyList<ToolDescriptorPayload>>(
                AllTools().Where(t => granted.Contains(t.Name)).ToList());
        }

        public async Task<ToolResultPayload> CallAsync(
            string agent, ToolCallPayload call, Action<string>? audit = null,
            CancellationToken cancellationToken = default)
        {
            var granted = await GrantsForAsync(agent, cancellationToken).ConfigureAwait(false);
            if (!granted.Contains(call.Name))
            {
                audit?.Invoke($"{agent} was refused '{call.Name}' (not granted)");
                return new ToolResultPayload(call.Name, $"'{call.Name}' is not available to you.", IsError: true);
            }

            audit?.Invoke($"{agent} called '{call.Name}'");
            Calls.Add((agent, call.Name));
            if (Gate is not null)
            {
                await Gate.Task.ConfigureAwait(false);
            }

            return new ToolResultPayload(call.Name, $"ran {call.Name} with {call.Arguments}", IsError: false);
        }

        public Task<IReadOnlyList<string>> GrantsForAsync(string agent, CancellationToken cancellationToken = default) =>
            Task.FromResult(_grants.TryGetValue(agent, out var g) ? g : []);

        public Task SetGrantsAsync(
            string agent, IReadOnlyList<string> tools, CancellationToken cancellationToken = default)
        {
            _grants[agent] = tools;
            return Task.CompletedTask;
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(
            _transport, _accounts, new DbServerStore(_database), files, tools: _tools);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private Task<BanterClient> ConnectAsync(string user) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, "pw");

    [Fact]
    public async Task AHumanClientCannotCallToolsAtAll()
    {
        await using var human = await ConnectAsync("human");

        var refusal = await Assert.ThrowsAsync<BanterErrorException>(
            () => human.CallToolAsync("read_file", """{"path":"/etc/passwd"}"""));

        // Clients hold no credentials and reach no upstream: that is the whole point of running
        // tools on the server.
        Assert.Equal("NOT_AN_AGENT", refusal.Code);
        Assert.Empty(_tools.Calls);
    }

    [Fact]
    public async Task AnAgentWithNoGrantsSeesNoTools()
    {
        await using var agent = await ConnectAsync("dagger");

        var listed = await agent.ListToolsAsync();

        // Absent, not refused: an agent must not be able to enumerate what it may not use.
        Assert.Empty(listed.Tools);
    }

    [Fact]
    public async Task AnAgentCannotCallAToolItWasNotGranted()
    {
        await using var admin = await ConnectAsync("admin");
        await using var agent = await ConnectAsync("dagger");
        await admin.SetToolGrantsAsync("dagger", ["gh_list_issues"]);

        var result = await agent.CallToolAsync("read_file", "{}");

        Assert.True(result.IsError);
        Assert.Empty(_tools.Calls);
    }

    [Fact]
    public async Task AGrantedToolRunsAndTheResultComesBack()
    {
        await using var admin = await ConnectAsync("admin");
        await using var agent = await ConnectAsync("dagger");
        await admin.SetToolGrantsAsync("dagger", ["gh_list_issues"]);

        var listed = await agent.ListToolsAsync();
        var result = await agent.CallToolAsync("gh_list_issues", """{"repo":"banter"}""");

        Assert.Equal("gh_list_issues", Assert.Single(listed.Tools).Name);
        Assert.False(result.IsError);
        Assert.Contains("""{"repo":"banter"}""", result.Content);
        Assert.Equal(("dagger", "gh_list_issues"), Assert.Single(_tools.Calls));
    }

    [Fact]
    public async Task GrantsAreScopedToOneAgent()
    {
        await using var admin = await ConnectAsync("admin");
        await using var scout = await ConnectAsync("scout");
        await admin.SetToolGrantsAsync("dagger", ["gh_list_issues", "read_file"]);

        var listed = await scout.ListToolsAsync();

        // Granting one agent must not widen another. Sharing by accident here would hand a
        // frontier agent the local-only tools deliberately kept away from it.
        Assert.Empty(listed.Tools);
    }

    [Fact]
    public async Task AnAgentCannotReadOrChangeGrants()
    {
        await using var agent = await ConnectAsync("dagger");

        var read = await Assert.ThrowsAsync<BanterErrorException>(() => agent.GetToolGrantsAsync("scout"));
        var write = await Assert.ThrowsAsync<BanterErrorException>(
            () => agent.SetToolGrantsAsync("dagger", ["read_file"]));

        Assert.Equal("NOT_ADMIN", read.Code);
        Assert.Equal("NOT_ADMIN", write.Code);
        Assert.Empty(await _tools.GrantsForAsync("dagger"));
    }

    [Fact]
    public async Task ANonAdminHumanCannotChangeGrantsEither()
    {
        await using var human = await ConnectAsync("human");

        var refusal = await Assert.ThrowsAsync<BanterErrorException>(
            () => human.SetToolGrantsAsync("dagger", ["read_file"]));

        Assert.Equal("NOT_ADMIN", refusal.Code);
    }

    [Fact]
    public async Task AnAdminSeesTheWholeCatalogueToGrantFrom()
    {
        await using var admin = await ConnectAsync("admin");

        var listed = await admin.ListToolsAsync();

        // The operator has to see tools that nobody holds yet — otherwise there is nothing to
        // grant from in the management panel.
        Assert.Equal(2, listed.Tools.Count);
        Assert.Contains(listed.Tools, t => t.ServerKey == "github");
    }

    [Fact]
    public async Task GrantsAreReplacedWholesaleSoARevokeReallyRevokes()
    {
        await using var admin = await ConnectAsync("admin");
        await admin.SetToolGrantsAsync("dagger", ["gh_list_issues", "read_file"]);

        var narrowed = await admin.SetToolGrantsAsync("dagger", ["read_file"]);
        var readBack = await admin.GetToolGrantsAsync("dagger");

        Assert.Equal(["read_file"], narrowed.Tools);
        Assert.Equal(["read_file"], readBack.Tools);
    }

    [Fact]
    public async Task GrantingAToolTheServerDoesNotHaveIsDropped()
    {
        await using var admin = await ConnectAsync("admin");

        var stored = await admin.SetToolGrantsAsync("dagger", ["gh_list_issues", "delete_everything"]);

        // A grant for a name nothing serves is a lie in the UI, and it would come alive the day
        // some upstream published that name.
        Assert.Equal(["gh_list_issues"], stored.Tools);
    }

    [Fact]
    public async Task AToolCallIsAnnouncedInTheRoomSoTheOperatorSeesIt()
    {
        await using var admin = await ConnectAsync("admin");
        await using var agent = await ConnectAsync("dagger");
        await admin.SetToolGrantsAsync("dagger", ["gh_list_issues"]);
        await admin.JoinAsync("#work");
        await agent.JoinAsync("#work");

        var seen = new TaskCompletionSource<string>();
        admin.MessageReceived += m =>
        {
            if (m.Text.StartsWith("[tool]", StringComparison.Ordinal))
            {
                seen.TrySetResult(m.Text);
            }
        };

        await agent.CallToolAsync("gh_list_issues", "{}", room: "#work");

        var announcement = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("dagger", announcement);
        Assert.Contains("gh_list_issues", announcement);
    }

    [Fact]
    public async Task ARefusedCallIsAnnouncedToo()
    {
        await using var admin = await ConnectAsync("admin");
        await using var agent = await ConnectAsync("dagger");
        await admin.JoinAsync("#work");
        await agent.JoinAsync("#work");

        var seen = new TaskCompletionSource<string>();
        admin.MessageReceived += m =>
        {
            if (m.Text.StartsWith("[tool]", StringComparison.Ordinal))
            {
                seen.TrySetResult(m.Text);
            }
        };

        await agent.CallToolAsync("read_file", "{}", room: "#work");

        // An agent reaching for something it does not hold is the more interesting event of the
        // two, so it must not be the quiet one.
        var announcement = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("refused", announcement);
    }

    [Fact]
    public async Task ASlowToolDoesNotStopTheRestOfTheServer()
    {
        await using var admin = await ConnectAsync("admin");
        await using var agent = await ConnectAsync("dagger");
        await using var human = await ConnectAsync("human");
        await admin.SetToolGrantsAsync("dagger", ["gh_list_issues"]);
        await human.JoinAsync("#work");

        var chatted = new TaskCompletionSource<MsgPayload>();
        human.MessageReceived += m => chatted.TrySetResult(m);

        // The engine is the single writer for every room. If a tool call were awaited on that
        // loop, this chat would not come back until the tool released the gate.
        _tools.Gate = new TaskCompletionSource();
        var slow = agent.CallToolAsync("gh_list_issues", "{}");
        await human.SendMessageAsync("#work", "still talking");

        var echo = await chatted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("still talking", echo.Text);

        _tools.Gate.SetResult();
        Assert.False((await slow).IsError);
    }
}
