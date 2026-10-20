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

namespace Banter.Integration.Tests;

/// <summary>
/// Whether a delegated room's delegator answers another agent that names it - the room's parley.
///
/// <para>By default it does while they are the only two agents in the room, so a pair can settle
/// something between them; with more agents it does not, so a room of many does not become a
/// committee. A room can be told otherwise either way.</para>
/// </summary>
public sealed class ParleyTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("root", "pw", isAgent: false, isAdmin: true)
        .AddUser("human", "pw")
        .AddUser("local-a", "pw", isAgent: true)
        .AddUser("scout", "pw", isAgent: true)
        .AddUser("third", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    /// <summary>Says the next line of its script each time it answers, and "noted" once the
    /// script runs out - so a conversation it is part of comes to an end on its own.</summary>
    private sealed class ScriptedAgent(BanterAgentOptions options, params string[] script) : BanterAgent(options)
    {
        private readonly Queue<string> _script = new(script);

        public List<string> Answered { get; } = [];

        public int AgentsIn(string room) => RosterFor(room).Count;

        public RoomParley ParleyIn(string room) => ParleyFor(room);

        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string line;
            lock (Answered)
            {
                Answered.Add($"{sender}: {prompt}");
                line = _script.Count > 0 ? _script.Dequeue() : "noted";
            }

            await Task.Yield();
            yield return line;
        }

        public int Count
        {
            get
            {
                lock (Answered)
                {
                    return Answered.Count;
                }
            }
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-parley-{Guid.NewGuid():N}");
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

    private BanterAgentOptions Agent(string nick) => new()
    {
        Server = _server.Endpoint,
        User = nick,
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat"],
    };

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(what);
    }

    private static async Task WaitForDelegatorAsync(BanterClient watcher, string nick)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await watcher.GetAgentsAsync("#main")).Agents.Any(a => a.IsDelegator && a.Nick == nick))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"{nick} never became delegator");
    }

    /// <summary>A delegator that hands the first thing to scout, and scout, which asks it back.
    /// Whether the delegator answers that is what every test here is about.</summary>
    private async Task<(ScriptedAgent Delegator, ScriptedAgent Scout)> PairAsync(BanterClient human)
    {
        var delegator = new ScriptedAgent(Agent("local-a"), "@scout what do you make of this?", "settled, then");
        await delegator.StartAsync(_transport);
        var scout = new ScriptedAgent(Agent("scout"), "@local-a I think it is fine - agreed?");
        await scout.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local-a");
        await UntilAsync(() => delegator.AgentsIn("#main") == 2, "the delegator never saw scout arrive");
        return (delegator, scout);
    }

    [Fact]
    public async Task TwoAgentsAloneInARoomTalkToEachOther()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        var (delegator, scout) = await PairAsync(human);
        await using var _1 = delegator;
        await using var _2 = scout;

        await human.SendMessageAsync("#main", "see what scout thinks");

        // Human -> delegator -> scout -> delegator, and then it stops: the delegator's second
        // line names nobody, so nobody answers it.
        await UntilAsync(() => delegator.Count == 2, "the delegator never answered scout");
        Assert.Contains("agreed?", delegator.Answered[1], StringComparison.Ordinal);
        await Task.Delay(1000);
        Assert.Equal(2, delegator.Count);
        Assert.Equal(1, scout.Count);
    }

    [Fact]
    public async Task WithAThirdAgentPresentTheDelegatorKeepsOutOfIt()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        var (delegator, scout) = await PairAsync(human);
        await using var _1 = delegator;
        await using var _2 = scout;
        await using var third = new ScriptedAgent(Agent("third"));
        await third.StartAsync(_transport);
        await UntilAsync(() => delegator.AgentsIn("#main") == 3, "the delegator never saw the third agent");

        await human.SendMessageAsync("#main", "see what scout thinks");

        await UntilAsync(() => scout.Count == 1, "scout was never handed the work");
        await Task.Delay(1500);
        Assert.Equal(1, delegator.Count);
    }

    [Fact]
    public async Task OpenLetsTheDelegatorAnswerAgentsInAFullRoom()
    {
        await using var admin = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "root", "pw");
        await admin.JoinAsync("#main");
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        var (delegator, scout) = await PairAsync(human);
        await using var _1 = delegator;
        await using var _2 = scout;
        await using var third = new ScriptedAgent(Agent("third"));
        await third.StartAsync(_transport);
        await UntilAsync(() => delegator.AgentsIn("#main") == 3, "the delegator never saw the third agent");

        Assert.Equal(RoomParley.Open, (await admin.SetRoomParleyAsync("#main", RoomParley.Open)).Parley);
        await UntilAsync(() => delegator.ParleyIn("#main") == RoomParley.Open, "the delegator was never told the parley changed");

        await human.SendMessageAsync("#main", "see what scout thinks");

        await UntilAsync(() => delegator.Count == 2, "the delegator never answered scout");
        Assert.Contains("agreed?", delegator.Answered[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoneKeepsTheDelegatorSilentTowardsAgentsEvenAsAPair()
    {
        await using var admin = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "root", "pw");
        await admin.JoinAsync("#main");
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        var (delegator, scout) = await PairAsync(human);
        await using var _1 = delegator;
        await using var _2 = scout;

        await admin.SetRoomParleyAsync("#main", RoomParley.None);
        await UntilAsync(() => delegator.ParleyIn("#main") == RoomParley.None, "the delegator was never told the parley changed");

        await human.SendMessageAsync("#main", "see what scout thinks");

        await UntilAsync(() => scout.Count == 1, "scout was never handed the work");
        await Task.Delay(1500);
        Assert.Equal(1, delegator.Count);
    }

    [Fact]
    public async Task OnlyAnAdminOrADelegatorMayChangeIt()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(() => human.SetRoomParleyAsync("#main", RoomParley.Open));
        Assert.Equal("NOT_ALLOWED", refused.Code);

        // Asking for what it already is reads it, and is for anybody.
        Assert.Equal(RoomParley.Pairs, (await human.SetRoomParleyAsync("#main", RoomParley.Pairs)).Parley);

        // It is said on joining, so a client knows it without asking.
        var told = new TaskCompletionSource<RoomParleyPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var admin = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "root", "pw");
        admin.RoomParleyChanged += p => told.TrySetResult(p);
        await admin.JoinAsync("#main");
        Assert.Equal(RoomParley.Pairs, (await told.Task.WaitAsync(Patience)).Parley);

        Assert.Equal(RoomParley.None, (await admin.SetRoomParleyAsync("#main", RoomParley.None)).Parley);
        Assert.Equal(RoomParley.None, (await human.ListRoomsAsync()).Rooms.Single(r => r.Name == "#main").Parley);
    }
}
