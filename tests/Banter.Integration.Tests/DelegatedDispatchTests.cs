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
/// Who actually speaks (PLAN §8a). In a delegated room one agent acts on human traffic and the
/// rest wait to be handed work; in a mention room every agent answers when named.
/// </summary>
public sealed class DelegatedDispatchTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw")
        .AddUser("local-a", "pw", isAgent: true)
        .AddUser("local-b", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    /// <summary>An agent that answers with a fixed line, so a reply is unambiguous.</summary>
    private sealed class EchoAgent(BanterAgentOptions options, string reply) : BanterAgent(options)
    {
        public List<string> Answered { get; } = [];

        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Answered.Add(prompt);
            await Task.Yield();
            yield return reply;
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-dispatch-{Guid.NewGuid():N}");
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

    private BanterAgentOptions AgentOptions(string user, bool local = true) => new()
    {
        Server = _server.Endpoint,
        User = user,
        Password = "pw",
        Rooms = ["#main"],
        Locality = local ? AgentLocality.Local : AgentLocality.Frontier,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat"],
    };

    /// <summary>Waits for a message matching <paramref name="predicate"/>, or returns null.</summary>
    private static async Task<MsgPayload?> WaitForMessageAsync(
        BanterClient client, string room, Func<MsgPayload, bool> predicate, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? Timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var history = await client.GetHistoryAsync(room, limit: 200);
            var hit = history.Messages.FirstOrDefault(predicate);
            if (hit is not null)
            {
                return hit;
            }

            await Task.Delay(25);
        }

        return null;
    }

    [Fact]
    public async Task InADelegatedRoomOnlyTheDelegatorAnswersAHumanMessage()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var first = new EchoAgent(AgentOptions("local-a"), "answer-from-a");
        await first.StartAsync(_transport);
        await using var second = new EchoAgent(AgentOptions("local-b"), "answer-from-b");
        await second.StartAsync(_transport);

        // local-a wins on join order and stays elected.
        await WaitForDelegatorAsync(human, "#main", "local-a");

        await human.SendMessageAsync("#main", "who can help me?");

        var answer = await WaitForMessageAsync(human, "#main", m => m.Text == "answer-from-a");
        Assert.NotNull(answer);

        // The non-delegator must stay quiet: it was never handed the work.
        var fromB = await WaitForMessageAsync(
            human, "#main", m => m.Text == "answer-from-b", within: TimeSpan.FromSeconds(2));
        Assert.Null(fromB);
        Assert.Empty(second.Answered);
    }

    [Fact]
    public async Task ANonDelegatorAnswersWhenTheDelegatorHandsItWork()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(AgentOptions("local-a"), "routing");
        await delegator.StartAsync(_transport);
        await using var worker = new EchoAgent(AgentOptions("local-b"), "worker-did-it");
        await worker.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "#main", "local-a");

        // The delegator naming the worker is the hand-off, and the only way a non-delegator speaks.
        await delegator.SayAsync("#main", "local-b please handle this");

        var answer = await WaitForMessageAsync(human, "#main", m => m.Text == "worker-did-it");
        Assert.NotNull(answer);
    }

    [Fact]
    public async Task AnAgentMentionedByAHumanStaysQuietInADelegatedRoom()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(AgentOptions("local-a"), "from-delegator");
        await delegator.StartAsync(_transport);
        await using var worker = new EchoAgent(AgentOptions("local-b"), "from-worker");
        await worker.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "#main", "local-a");

        // Naming an agent directly does not bypass the delegator - otherwise "delegated" would
        // only hold until someone typed a nick.
        await human.SendMessageAsync("#main", "local-b can you do this?");

        var fromWorker = await WaitForMessageAsync(
            human, "#main", m => m.Text == "from-worker", within: TimeSpan.FromSeconds(2));
        Assert.Null(fromWorker);
        Assert.Empty(worker.Answered);
    }

    [Fact]
    public async Task InMentionModeEveryNamedAgentAnswers()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        await human.SetRoomModeAsync("#main", RoomDispatchMode.Mention);

        await using var a = new EchoAgent(AgentOptions("local-a"), "answer-a");
        await a.StartAsync(_transport);
        await using var b = new EchoAgent(AgentOptions("local-b"), "answer-b");
        await b.StartAsync(_transport);

        await human.SendMessageAsync("#main", "local-b are you there?");

        Assert.NotNull(await WaitForMessageAsync(human, "#main", m => m.Text == "answer-b"));

        // And the one not named stays out of it.
        Assert.Null(await WaitForMessageAsync(
            human, "#main", m => m.Text == "answer-a", within: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task WithNoDelegatorElectedTheRoomFallsBackToMentionBehaviour()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        // A frontier agent in a (default) sensitive room is never elected, so the room has no
        // delegator - it must still be usable rather than silent.
        await using var frontier = new EchoAgent(AgentOptions("local-a", local: false), "frontier-answer");
        await frontier.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "#main", null);

        await human.SendMessageAsync("#main", "local-a are you there?");

        Assert.NotNull(await WaitForMessageAsync(human, "#main", m => m.Text == "frontier-answer"));
    }

    [Fact]
    public async Task TheDelegatorDoesNotAnswerAnotherAgentsChatter()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(AgentOptions("local-a"), "delegator-answer");
        await delegator.StartAsync(_transport);
        await using var other = new EchoAgent(AgentOptions("local-b"), "other-answer");
        await other.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "#main", "local-a");

        // Two agents answering each other is the loop the server's loop-breaker exists to stop;
        // the delegator should not start one in the first place.
        await other.SayAsync("#main", "just thinking out loud");

        Assert.Null(await WaitForMessageAsync(
            human, "#main", m => m.Text == "delegator-answer", within: TimeSpan.FromSeconds(2)));
    }

    private static async Task WaitForDelegatorAsync(BanterClient observer, string room, string? expected)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        string? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var agents = await observer.GetAgentsAsync(room);
            last = agents.Agents.FirstOrDefault(a => a.IsDelegator)?.Nick;
            if (string.Equals(last, expected, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Expected delegator '{expected ?? "(none)"}', saw '{last ?? "(none)"}'.");
    }
}
