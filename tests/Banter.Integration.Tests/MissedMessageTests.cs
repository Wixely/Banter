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
/// What an agent knows about a room it was not listening to.
///
/// <para>Banter keeps every message, so an agent that has been away can be told what it missed —
/// unlike IRC, where it simply did not happen. The danger that comes with that is the whole
/// subject of these tests: a backlog of requests replayed through the ordinary path would have the
/// agent answer every question it was asked while it was gone, hours late and all at once.</para>
/// </summary>
public sealed class MissedMessageTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw")
        .AddUser("scribe", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private FileStore _files = null!;
    private BanterServer _server = null!;

    /// <summary>Records what it was told it missed, and answers anything it is asked live.</summary>
    private sealed class WatchfulAgent(BanterAgentOptions options) : BanterAgent(options)
    {
        public List<string> Missed { get; } = [];
        public List<string> Answered { get; } = [];

        /// <summary>Rejoins the room, which is what a reconnect does to the client underneath.</summary>
        public Task RejoinAsync(string room) => Client.JoinAsync(room);

        /// <summary>Fires when the client has redialled and rejoined, so a test can wait for it.</summary>
        public Task Reconnected
        {
            get
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Client.Reconnected += () => done.TrySetResult();
                return done.Task;
            }
        }

        protected override void OnMissedMessages(string room, IReadOnlyList<MsgPayload> missed)
        {
            lock (Missed)
            {
                Missed.AddRange(missed.Select(m => m.Text));
            }
        }

        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            lock (Answered)
            {
                Answered.Add(prompt);
            }

            yield return "ANSWERED";
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-missed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        _files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), _files);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private BanterAgentOptions Options(int backfill = 50) => new()
    {
        Server = _server.Endpoint,
        User = "scribe",
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat"],
        BackfillOnJoin = backfill,
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

    [Fact]
    public async Task AnAgentArrivingLateIsToldWhatItMissedAndAnswersNoneOfIt()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        // A conversation with a direct request in it, before the agent exists at all.
        await human.SendMessageAsync("#main", "morning all");
        await human.SendMessageAsync("#main", "@scribe can you summarise yesterday?");
        await human.SendMessageAsync("#main", "never mind, found it");

        await using var scribe = new WatchfulAgent(Options());
        await scribe.StartAsync(_transport);

        await UntilAsync(() => scribe.Missed.Count >= 3, "the agent was never told what it missed");
        output.WriteLine($"missed: {string.Join(" | ", scribe.Missed)}");
        Assert.Contains(scribe.Missed, t => t.Contains("summarise yesterday"));

        // The point: it knows about the request and did not act on it. Answering an @mention from
        // before it arrived is the failure this whole design is shaped to prevent.
        await Task.Delay(500);
        Assert.Empty(scribe.Answered);
    }

    [Fact]
    public async Task ItStillAnswersWhatIsSaidToItAfterwards()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        await human.SendMessageAsync("#main", "@scribe are you there?");

        await using var scribe = new WatchfulAgent(Options());
        await scribe.StartAsync(_transport);
        await UntilAsync(() => scribe.Missed.Count >= 1, "nothing was backfilled");

        // Backfilling must not leave it deaf: the next thing said to it is live, and answered.
        await human.SendMessageAsync("#main", "@scribe now I really do need you");

        await UntilAsync(() => scribe.Answered.Count == 1, "the agent stopped answering live messages");
        Assert.Contains("really do need you", scribe.Answered[0]);
    }

    [Fact]
    public async Task ReadingBackTwiceDoesNotObserveTheSameConversationTwice()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        await human.SendMessageAsync("#main", "first");

        await using var scribe = new WatchfulAgent(Options());
        await scribe.StartAsync(_transport);
        await UntilAsync(() => scribe.Missed.Count == 1, "the first line was not backfilled");

        // A second room join, as a rejoin would do. Only the gap should come back — a flapping
        // connection that re-observed the same conversation every cycle would fill the agent's
        // context with echoes of itself.
        await human.SendMessageAsync("#main", "second");
        await scribe.RejoinAsync("#main");

        await Task.Delay(400);
        output.WriteLine($"missed: {string.Join(" | ", scribe.Missed)}");
        Assert.DoesNotContain("first", scribe.Missed.Skip(1));
    }

    [Fact]
    public async Task AReconnectDoesNotReplayWhatWasAlreadyHeard()
    {
        // A real drop and redial. The gap itself is covered by the arriving-late test above —
        // an agent whose process restarts is the same code path and the same situation. What is
        // unique to a reconnect is that the room's recent history is mostly things this agent
        // DID hear, and reading it back as though it were missed would fill its context with
        // echoes of the conversation it just had. A flapping connection would do it repeatedly.
        var port = _server.Endpoint.Port;
        await using var scribe = new WatchfulAgent(Options());
        await scribe.StartAsync(_transport);
        var reconnected = scribe.Reconnected;

        await using (var early = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw"))
        {
            await early.JoinAsync("#main");
            await early.SendMessageAsync("#main", "@scribe hello");
            await UntilAsync(() => scribe.Answered.Count == 1, "the agent never answered while connected");
        }

        var heardBefore = scribe.Missed.Count;

        // Take the server away and bring it back on the same port; the client redials by itself.
        await _server.DisposeAsync();
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), _files);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _server.StartAsync(new Uri($"tcp://127.0.0.1:{port}"));
                break;
            }
            catch (System.Net.Sockets.SocketException) when (attempt < 20)
            {
                await Task.Delay(100);
            }
        }

        await reconnected.WaitAsync(Patience);
        await Task.Delay(500);

        output.WriteLine($"missed before {heardBefore}, after {scribe.Missed.Count}: " +
                         string.Join(" | ", scribe.Missed));

        // Nothing it already heard comes back as missed, and its one answer is still its one.
        Assert.DoesNotContain(scribe.Missed, t => t.Contains("@scribe hello"));
        Assert.Single(scribe.Answered);
    }

    [Fact]
    public async Task BackfillCanBeTurnedOff()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        await human.SendMessageAsync("#main", "something it will never hear about");

        await using var scribe = new WatchfulAgent(Options(backfill: 0));
        await scribe.StartAsync(_transport);

        await Task.Delay(500);
        Assert.Empty(scribe.Missed);
    }
}
