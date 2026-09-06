using System.Runtime.CompilerServices;
using Banter.Agents.Sdk;
using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Integration.Tests;

/// <summary>
/// A delegator that can act on the room rather than only talk about it: open a side room, put
/// agents in it with their own instructions, and escalate to a human when something is outside
/// what it was asked to do.
///
/// <para>The whole flow this is shaped around: a request arrives, the delegator splits it between
/// two agents in a side room, they confer there, one of them needs an agent that is not present,
/// a human says yes, and the delegator is the one that acts on the yes.</para>
/// </summary>
public sealed class DelegatorRoomToolsTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw", isAgent: false, isAdmin: true)
        .AddUser("local", "pw", isAgent: true)
        .AddUser("local-2", "pw", isAgent: true)
        .AddUser("local-3", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    /// <summary>An agent whose "model" calls whichever tools a test queues up for it.</summary>
    private sealed class ToolingAgent(BanterAgentOptions options) : BanterAgent(options)
    {
        public Queue<(string Tool, string Args)> Plan { get; } = new();
        public List<string> Results { get; } = [];

        public IReadOnlyList<string> ToolNamesIn(string room) =>
            [.. ToolsFor(room).Select(t => t.Name)];

        public List<string> Seen { get; } = [];

        protected override bool ShouldRespond(MsgPayload m)
        {
            var yes = base.ShouldRespond(m);
            lock (Seen)
            {
                Seen.Add($"{m.Sender}: {m.Text} -> {yes}");
            }

            return yes;
        }

        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (Plan.Count > 0)
            {
                var (tool, args) = Plan.Dequeue();
                var result = await InvokeToolAsync(tool, args, room, cancellationToken).ConfigureAwait(false);
                lock (Results)
                {
                    Results.Add(result.Content);
                }

            }

            yield return "ok";
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-roomtools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), files);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// An agent. Deliberately without <c>Routing</c>: a delegator that routes hands the message to
    /// somebody else BEFORE RespondAsync runs, so a routing delegator never reaches its own tools.
    /// Routing is covered in DelegatorRoutingTests; what is under test here is what a delegator
    /// can do once it decides to act itself.
    /// </summary>
    private BanterAgentOptions Agent(string nick) => new()
    {
        Server = _server.Endpoint,
        User = nick,
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat", "notes"],
    };

    private async Task<BanterClient> HumanAsync()
    {
        var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        return human;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(what);
    }

    /// <summary>The room name out of a tool result, the way a model would read it back.</summary>
    private static string RoomNameIn(string result)
    {
        var at = result.IndexOf('#');
        Assert.True(at >= 0, $"no room name in '{result}'");
        var end = at;
        while (end < result.Length && !char.IsWhiteSpace(result[end]) && result[end] != '.')
        {
            end++;
        }

        return result[at..end];
    }

    private async Task WaitForDelegatorAsync(BanterClient watcher, string nick) =>
        await UntilAsync(
            async () => (await watcher.GetAgentsAsync("#main")).Agents.Any(a => a.IsDelegator && a.Nick == nick),
            $"'{nick}' never became delegator");

    [Fact]
    public async Task OnlyTheDelegatorIsOfferedTheRoomTools()
    {
        await using var human = await HumanAsync();
        await using var boss = new ToolingAgent(Agent("local"));
        await boss.StartAsync(_transport);
        await using var worker = new ToolingAgent(Agent("local-2"));
        await worker.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        var bossTools = boss.ToolNamesIn("#main");
        var workerTools = worker.ToolNamesIn("#main");
        output.WriteLine($"delegator: {string.Join(", ", bossTools)}");
        output.WriteLine($"worker:    {string.Join(", ", workerTools)}");

        // Rearranging the room is the delegator's, and matches what the server allows — offering
        // a tool that would be refused invites a model to keep trying it.
        Assert.Contains("open_side_room", bossTools);
        Assert.Contains("invite_agent", bossTools);
        Assert.DoesNotContain("open_side_room", workerTools);
        Assert.DoesNotContain("invite_agent", workerTools);

        // Asking for a decision is everybody's: a worker that hits something outside its brief is
        // exactly who needs to.
        Assert.Contains("ask_operator", bossTools);
        Assert.Contains("ask_operator", workerTools);
    }

    [Fact]
    public async Task TheDelegatorSplitsWorkIntoASideRoomWithAnInstructionEach()
    {
        await using var human = await HumanAsync();
        await using var boss = new ToolingAgent(Agent("local"));
        await boss.StartAsync(_transport);
        await using var one = new ToolingAgent(Agent("local-2"));
        await one.StartAsync(_transport);
        await using var two = new ToolingAgent(Agent("local-3"));
        await two.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        // The room's name carries a random suffix, so it cannot be known in advance — which is
        // exactly why open_side_room returns it. Read back from the result, as a model would.
        boss.Plan.Enqueue(("open_side_room", """{"purpose":"the release notes"}"""));
        await human.SendMessageAsync("#main", "@local write the release notes");
        await UntilAsync(() => Task.FromResult(boss.Results.Count == 1), "the room was never opened");

        var room = RoomNameIn(boss.Results[0]);
        output.WriteLine($"opened {room}");

        boss.Plan.Enqueue(("invite_agent",
            $$"""{"agent":"local-2","room":"{{room}}","instruction":"collect the merged PRs since v1"}"""));
        boss.Plan.Enqueue(("invite_agent",
            $$"""{"agent":"local-3","room":"{{room}}","instruction":"draft the summary from what local-2 collects"}"""));
        await human.SendMessageAsync("#main", "@local carry on");
        await UntilAsync(() => Task.FromResult(boss.Results.Count == 3), "the invitations never ran");

        // The room exists, both agents are in it, and each was told its own slice — an agent told
        // the whole task does the whole task, and two of those duplicate each other.
        await human.JoinAsync(room);
        var members = await human.GetMembersAsync(room);
        Assert.Contains(members.Members, m => m.Nick == "local-2");
        Assert.Contains(members.Members, m => m.Nick == "local-3");

        var said = await human.GetHistoryAsync(room, limit: 100);
        Assert.Contains(said.Messages, m => m.Text.Contains("@local-2") && m.Text.Contains("merged PRs"));
        Assert.Contains(said.Messages, m => m.Text.Contains("@local-3") && m.Text.Contains("draft the summary"));

        // And it is a room they may confer in, which #main is not. Re-setting returns the mode,
        // which is the only way to read it back and is idempotent.
        Assert.Equal(RoomDispatchMode.Collaborate,
            (await human.SetRoomModeAsync(room, RoomDispatchMode.Collaborate)).Mode);
    }

    [Fact]
    public async Task AWorkerEscalatesAndTheDelegatorActsOnTheAnswer()
    {
        // The flow end to end: a worker needs an agent that is not here, asks, a human says yes,
        // and the delegator is the one that can act on the yes.
        await using var human = await HumanAsync();
        await using var boss = new ToolingAgent(Agent("local"));
        await boss.StartAsync(_transport);
        await using var worker = new ToolingAgent(Agent("local-2"));
        await worker.StartAsync(_transport);
        await using var extra = new ToolingAgent(Agent("local-3"));
        await extra.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        // The worker asks. It goes into the room as an ordinary message as well as a set of
        // choices, so a human reading the history simply reads it.
        worker.Plan.Enqueue(("ask_operator",
            """
            {"questions":[{"key":"extra","header":"Extra agent",
              "question":"this needs someone who can search the web. May local-3 join?",
              "options":[{"value":"yes","label":"Yes"},{"value":"no","label":"No"}]}]}
            """));
        await human.SendMessageAsync("#main", "@local-2 get started");
        await UntilAsync(
            async () => (await human.GetHistoryAsync("#main", limit: 100)).Messages
                .Any(m => m.Text.StartsWith("[asking]", StringComparison.Ordinal)),
            "the worker never asked");

        var asked = (await human.GetHistoryAsync("#main", limit: 100)).Messages
            .First(m => m.Text.StartsWith("[asking]", StringComparison.Ordinal));
        output.WriteLine(asked.Text);
        Assert.Contains("search the web", asked.Text);

        // The human answers the delegator, which is the one that can do anything about it.
        boss.Plan.Enqueue(("open_side_room", """{"purpose":"web search"}"""));
        await human.SendMessageAsync("#main", "@local yes, add local-3");
        await UntilAsync(() => Task.FromResult(boss.Results.Count == 1), "the delegator never opened a room");

        var side = RoomNameIn(boss.Results[0]);
        boss.Plan.Enqueue(("invite_agent",
            $$"""{"agent":"local-3","room":"{{side}}","instruction":"search for what local-2 needs"}"""));
        await human.SendMessageAsync("#main", "@local and bring them in");
        await UntilAsync(() => Task.FromResult(boss.Results.Count == 2), "the delegator never acted on the answer");

        await human.JoinAsync(side);
        var members = await human.GetMembersAsync(side);
        Assert.Contains(members.Members, m => m.Nick == "local-3");
    }

    [Fact]
    public async Task AWorkerCannotRearrangeTheRoomEvenIfItTries()
    {
        await using var human = await HumanAsync();
        await using var boss = new ToolingAgent(Agent("local"));
        await boss.StartAsync(_transport);
        await using var worker = new ToolingAgent(Agent("local-2"));
        await worker.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        // Not offered it, and refused if it calls it anyway — the catalogue is a courtesy, the
        // server is the rule.
        worker.Plan.Enqueue(("open_side_room", """{"purpose":"my own room"}"""));
        await human.SendMessageAsync("#main", "@local-2 go");
        await UntilAsync(() => Task.FromResult(worker.Results.Count == 1), "the worker never tried");

        output.WriteLine(worker.Results[0]);

        // Told plainly that it does not have it, and what it does have — not "this server has no
        // tools", which is a different and misleading fact, and not an exception that ends the
        // turn. An agent reaching for a tool it lacks should carry on without it.
        Assert.Contains("no tool called 'open_side_room'", worker.Results[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ask_operator", worker.Results[0], StringComparison.OrdinalIgnoreCase);
    }
}
