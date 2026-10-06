using System.Text.Json;
using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Banter.Server.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Integration.Tests;

/// <summary>
/// HUB_INSPECT over the wire: the hubs page asking a running server what it defers to.
///
/// <para>The broker's own answer is covered in Banter.Server.Tests. What is only true here is the
/// round trip - that the reply deserialises as a report rather than throwing, that a non-admin is
/// refused rather than handed a map of the estate, and that a server with no tool backend says so
/// instead of answering with an empty list. The page distinguishes those two (PLAN 8c), so the
/// difference has to be real on the wire and not merely in the client.</para>
/// </summary>
public sealed class HubInspectTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();

    private string _root = null!;
    private BanterDatabase _database = null!;
    private DbAccountStore _accounts = null!;
    private McpToolBroker? _broker;
    private FakeHub _hub = new();
    private BanterServer _server = null!;

    /// <summary>A hub that answers the management calls the broker makes while provisioning, so
    /// an agent can be given an identity without an MCPHub to talk to.</summary>
    private sealed class FakeHub : IHubAdmin
    {
        private int _issued;

        public Dictionary<string, string[]> Grants { get; } = new(StringComparer.Ordinal);

        /// <summary>Every management tool this hub was asked for, in order - so a test can say
        /// what the server actually did there rather than only what came back.</summary>
        public List<string> Calls { get; } = [];

        /// <summary>The keys this hub has issued, newest last. It only ever shows one once.</summary>
        public List<string> Issued { get; } = [];

        public Task<JsonElement> CallAsync(
            string tool,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            var json = tool switch
            {
                "users__create" => Mint($"user-{++_issued}"),
                "users__rotate_key" => Mint((string)arguments!["user"]!),
                "users__delete" => Delete(arguments!),
                "permissions__set_grants" => Set(arguments!),
                "permissions__list_grants" => List(),
                _ => throw new HubAdminException("hub.unavailable", $"no such tool '{tool}'"),
            };

            return Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
        }

        /// <summary>A key for a user, new or existing. The id comes back with it because that is
        /// the shape the provisioner reads, and a rotation keeps the id it was given.</summary>
        private string Mint(string userId)
        {
            var key = $"mcphub_k{Issued.Count + 1}";
            Issued.Add(key);
            return $$"""{"user":{"id":"{{userId}}"},"key":"{{key}}"}""";
        }

        private string Delete(IReadOnlyDictionary<string, object?> arguments)
        {
            Grants.Remove((string)arguments["user"]!);
            return """{"message":"deleted"}""";
        }

        private string Set(IReadOnlyDictionary<string, object?> arguments)
        {
            Grants[(string)arguments["user"]!] = [.. (IReadOnlyList<string>)arguments["tools"]!];
            return """{"message":"saved"}""";
        }

        private string List()
        {
            var entries = Grants.Select(g =>
                $$"""{"userId":"{{g.Key}}","tools":[{{string.Join(",", g.Value.Select(t => $"\"{t}\""))}}]}""");
            return $$"""{"grants":[{{string.Join(",", entries)}}]}""";
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Started per test rather than in InitializeAsync, because one of these needs a server with
    /// NO tool backend and that is a different server, not a different call.
    /// </summary>
    private async Task StartAsync(bool withTools)
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-hubs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();

        _accounts = new DbAccountStore(_database);
        await _accounts.CreateUserAsync("root", "pw", isAdmin: true);
        await _accounts.CreateUserAsync("nell", "pw");

        if (withTools)
        {
            var options = new McpOptions
            {
                Upstreams =
                [
                    new McpUpstreamConfig
                    {
                        // A URL nothing is listening on: the hub is unreachable for TOOLS, which
                        // is the state the page most needs to report, while the provisioning
                        // calls still go through the injected admin.
                        Key = "hub", DisplayName = "MCPHub",
                        Url = "http://127.0.0.1:1/mcp", PerAgentIdentity = true,
                    },
                ],
            };

            _hub = new FakeHub();
            _broker = new McpToolBroker(
                options, new ToolGrantStore(_database), new HubIdentityStore(_database),
                loggerFactory: null, hubAdmin: _ => _hub);
            await _broker.SetGrantsAsync("scribe", ["hub__recipes__list", "hub__recipes__get"]);
        }

        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(
            _transport, _accounts, new DbServerStore(_database), files,
            tasks: new TaskStore(_database),
            tools: _broker,
            identities: new AgentIdentityStore(_database),
            accountAdmin: _accounts);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        if (_broker is not null)
        {
            await _broker.DisposeAsync();
        }

        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private Task<BanterClient> ConnectAsync(string user, string pass) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, pass);

    [Fact]
    public async Task AnAdminGetsTheHubsAndWhatEachAgentHoldsOnThem()
    {
        await StartAsync(withTools: true);
        await using var admin = await ConnectAsync("root", "pw");

        var hubs = await admin.InspectHubsAsync().WaitAsync(Patience);
        var hub = Assert.Single(hubs);
        output.WriteLine($"{hub.Key} ({hub.DisplayName}): {hub.State}, {hub.ToolCount} tools, "
            + $"administrable={hub.Administrable}, agents={hub.Agents.Count}");

        Assert.Equal("hub", hub.Key);
        Assert.Equal("MCPHub", hub.DisplayName);

        var agent = Assert.Single(hub.Agents);
        output.WriteLine($"  {agent.Agent} is {agent.UserId}: {string.Join(", ", agent.Tools)}");
        Assert.Equal("scribe", agent.Agent);
        Assert.Equal(["hub__recipes__get", "hub__recipes__list"], agent.Tools.Order(StringComparer.Ordinal));

        // The key the hub minted stays on the server. Checked against the serialised REPLY rather
        // than the payload type, because this is the hop where a leak would actually happen.
        Assert.DoesNotContain("mcphub_k", JsonSerializer.Serialize(hubs), StringComparison.Ordinal);
    }

    /// <summary>
    /// It names every agent that has an identity on a hub and what each holds, which is a map of
    /// the estate - so it is admin-only, and refused rather than quietly emptied.
    /// </summary>
    [Fact]
    public async Task AMemberIsRefused()
    {
        await StartAsync(withTools: true);
        await using var member = await ConnectAsync("nell", "pw");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(
            () => member.InspectHubsAsync().WaitAsync(Patience));

        output.WriteLine($"{refused.Code}: {refused.Message}");
        Assert.Equal("NOT_ADMIN", refused.Code);
    }

    /// <summary>
    /// "No hubs" and "I could not ask" must not look the same: the first is a deployment choice
    /// and the second is something to fix. The page puts the refusal where the count goes.
    /// </summary>
    [Fact]
    public async Task AServerWithNoToolBackendRefusesRatherThanReportingNothing()
    {
        await StartAsync(withTools: false);
        await using var admin = await ConnectAsync("root", "pw");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(
            () => admin.InspectHubsAsync().WaitAsync(Patience));

        output.WriteLine($"{refused.Code}: {refused.Message}");
        Assert.Equal("NO_TOOLS", refused.Code);
    }

    /// <summary>
    /// Rotating asks the hub for a new key, keeps the same user there, and hands back the hubs as
    /// they are afterwards - not an Ok the page would have to follow with a second question.
    ///
    /// <para>What the client gets back must still carry no key. That is the whole reason the
    /// server mints them: a rotation that showed the new key to every admin client would have
    /// moved the secret into the one place it was being kept out of.</para>
    /// </summary>
    [Fact]
    public async Task RotatingIssuesANewKeyWithoutShowingAnyone()
    {
        await StartAsync(withTools: true);
        await using var admin = await ConnectAsync("root", "pw");

        var before = Assert.Single(await admin.InspectHubsAsync().WaitAsync(Patience)).Agents;
        var userId = Assert.Single(before).UserId;

        var after = await admin.RotateHubKeyAsync("hub", "scribe").WaitAsync(Patience);
        var agent = Assert.Single(Assert.Single(after).Agents);
        output.WriteLine($"hub saw: {string.Join(", ", _hub.Calls)}");
        output.WriteLine($"issued: {string.Join(", ", _hub.Issued)}");

        Assert.Contains("users__rotate_key", _hub.Calls);
        Assert.Equal(userId, agent.UserId);          // the same user, a different key
        Assert.Equal(2, _hub.Issued.Count);
        Assert.DoesNotContain("mcphub_k", JsonSerializer.Serialize(after), StringComparison.Ordinal);

        // And the grants it had there are untouched: rotating a key is not a change of access.
        Assert.Equal(["hub__recipes__get", "hub__recipes__list"], agent.Tools.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Removing deletes the user on the hub as well as forgetting the key here - a user left
    /// behind holds a key this server no longer tracks - and the agent is gone from the report
    /// that comes back.
    /// </summary>
    [Fact]
    public async Task RemovingDeletesTheUserThereAndForgetsItHere()
    {
        await StartAsync(withTools: true);
        await using var admin = await ConnectAsync("root", "pw");

        var after = await admin.ForgetHubIdentityAsync("hub", "scribe").WaitAsync(Patience);

        output.WriteLine($"hub saw: {string.Join(", ", _hub.Calls)}");
        Assert.Contains("users__delete", _hub.Calls);
        Assert.Empty(Assert.Single(after).Agents);

        // Asked again from a clean read, in case the reply was merely built without it.
        Assert.Empty(Assert.Single(await admin.InspectHubsAsync().WaitAsync(Patience)).Agents);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMemberCanNeitherRotateNorRemove(bool rotate)
    {
        await StartAsync(withTools: true);
        await using var member = await ConnectAsync("nell", "pw");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(
            () => (rotate
                ? member.RotateHubKeyAsync("hub", "scribe")
                : member.ForgetHubIdentityAsync("hub", "scribe")).WaitAsync(Patience));

        output.WriteLine($"{refused.Code}: {refused.Message}");
        Assert.Equal("NOT_ADMIN", refused.Code);

        // Refused before anything happened, not after.
        Assert.DoesNotContain("users__rotate_key", _hub.Calls);
        Assert.DoesNotContain("users__delete", _hub.Calls);
    }

    /// <summary>
    /// A hub this server does not have is refused by name. These two verbs take a hub from the
    /// client, so an unknown key must not reach a provisioner that would dutifully prefix tool
    /// names with it and ask an upstream that is not there.
    /// </summary>
    [Fact]
    public async Task AnUnknownHubIsRefusedByName()
    {
        await StartAsync(withTools: true);
        await using var admin = await ConnectAsync("root", "pw");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(
            () => admin.RotateHubKeyAsync("elsewhere", "scribe").WaitAsync(Patience));

        output.WriteLine($"{refused.Code}: {refused.Message}");
        Assert.Equal("HUB_REFUSED", refused.Code);
        Assert.Contains("elsewhere", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Naming neither is a refusal rather than a no-op, because a client that sends an
    /// empty agent has a bug and silence would hide it.</summary>
    [Fact]
    public async Task NamingNobodyIsRefused()
    {
        await StartAsync(withTools: true);
        await using var admin = await ConnectAsync("root", "pw");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(
            () => admin.RotateHubKeyAsync("hub", "").WaitAsync(Patience));

        Assert.Equal("BAD_AGENT", refused.Code);
    }
}
