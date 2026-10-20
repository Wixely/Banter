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
/// Naming an agent with "@" reaches it directly, delegated room or not — asking someone by name and
/// being answered by whoever holds the room is not what anybody means by it.
///
/// <para>What that bypasses is <b>who answers</b>. It must not bypass <b>what may leave</b>, or the
/// clearance model would come undone by typing a character.</para>
/// </summary>
public sealed class MentionBypassesDelegationTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw")
        .AddUser("local-a", "pw", isAgent: true)
        .AddUser("scout", "pw", isAgent: true)
        .AddUser("scout-2", "pw", isAgent: true)
        .AddUser("third", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

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
        _root = Path.Combine(Path.GetTempPath(), $"banter-mention-{Guid.NewGuid():N}");
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

    private BanterAgentOptions Options(
        string user, AgentLocality locality, DataSensitivity clearance = DataSensitivity.Sensitive) => new()
    {
        Server = _server.Endpoint,
        User = user,
        Password = "pw",
        Rooms = ["#main"],
        Locality = locality,
        Clearance = clearance,
        Skills = ["chat"],
    };

    private static async Task<MsgPayload?> WaitForAsync(
        BanterClient client, Func<MsgPayload, bool> predicate, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? Patience);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var history = await client.GetHistoryAsync("#main", limit: 200);
            if (history.Messages.FirstOrDefault(predicate) is { } hit)
            {
                return hit;
            }

            await Task.Delay(25);
        }

        return null;
    }

    private static async Task WaitForDelegatorAsync(BanterClient client, string nick)
    {
        var seen = await WaitForAsync(
            client, m => m.Text.Contains(nick, StringComparison.Ordinal) && m.Text.Contains("delegator", StringComparison.Ordinal));
        Assert.NotNull(seen);
    }

    [Fact]
    public async Task AMentionReachesTheAgentNamedRatherThanTheDelegator()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(Options("local-a", AgentLocality.Local), "from-the-delegator");
        await delegator.StartAsync(_transport);
        await using var other = new EchoAgent(Options("scout", AgentLocality.Local), "from-scout");
        await other.StartAsync(_transport);

        await WaitForDelegatorAsync(human, "local-a");

        await human.SendMessageAsync("#main", "@scout what do you make of this?");

        Assert.NotNull(await WaitForAsync(human, m => m.Text == "from-scout"));
        Assert.Contains("what do you make of this?", other.Answered.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDelegatorStaysOutOfAQuestionAddressedToAnotherAgent()
    {
        // Reported from a real room: "@dev who are you?" was answered by dev AND by the
        // delegator, which said dev would have to answer for itself - a second reply to a
        // question nobody had asked it, from the one agent that could not answer it.
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(Options("local-a", AgentLocality.Local), "from-the-delegator");
        await delegator.StartAsync(_transport);
        await using var other = new EchoAgent(Options("scout", AgentLocality.Local), "from-scout");
        await other.StartAsync(_transport);

        await WaitForDelegatorAsync(human, "local-a");

        await human.SendMessageAsync("#main", "@scout who are you?");

        Assert.NotNull(await WaitForAsync(human, m => m.Text == "from-scout"));

        // Settle: the delegator answering late is the same bug arriving slowly.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(delegator.Answered);
        Assert.Null(await WaitForAsync(
            human, m => m.Text == "from-the-delegator", TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task NamingSomebodyWhoIsNotHereStillReachesTheDelegator()
    {
        // The other half, and why this checks the roster rather than just looking for an "@".
        // A name that belongs to nobody in the room - a typo, or an agent that has left - is a
        // request only the delegator can do anything about, so it must not silence it.
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(Options("local-a", AgentLocality.Local), "from-the-delegator");
        await delegator.StartAsync(_transport);
        await using var other = new EchoAgent(Options("scout", AgentLocality.Local), "from-scout");
        await other.StartAsync(_transport);

        await WaitForDelegatorAsync(human, "local-a");

        await human.SendMessageAsync("#main", "@nobody are you there?");

        Assert.NotNull(await WaitForAsync(human, m => m.Text == "from-the-delegator"));
        Assert.Empty(other.Answered);
    }

    [Fact]
    public async Task OnlyTheDelegatorMayHandWorkToAnotherAgent()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(Options("local-a", AgentLocality.Local), "from-the-delegator");
        await delegator.StartAsync(_transport);
        await using var scout = new EchoAgent(Options("scout", AgentLocality.Local), "from-scout");
        await scout.StartAsync(_transport);
        await using var third = new EchoAgent(Options("third", AgentLocality.Local), "from-third");
        await third.StartAsync(_transport);

        await WaitForDelegatorAsync(human, "local-a");

        // scout is not the delegator. If its "@" summoned another agent, two of them could hand
        // work back and forth around the election indefinitely — which is what electing one is for.
        await scout.SayAsync("#main", "@third could you look at this?");
        Assert.Null(await WaitForAsync(human, m => m.Text == "from-third", TimeSpan.FromSeconds(2)));

        // The delegator naming it, on the other hand, is the hand-off, and still works.
        await delegator.SayAsync("#main", "@third could you look at this?");
        Assert.NotNull(await WaitForAsync(human, m => m.Text == "from-third"));
    }

    [Fact]
    public async Task AMentionInPassingIsNotASummons()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(Options("local-a", AgentLocality.Local), "from-the-delegator");
        await delegator.StartAsync(_transport);
        await using var other = new EchoAgent(Options("scout", AgentLocality.Local), "from-scout");
        await other.StartAsync(_transport);

        await WaitForDelegatorAsync(human, "local-a");

        // Naming an agent in a sentence addresses the room, not the agent. Otherwise every agent
        // answers every sentence its name appears in.
        await human.SendMessageAsync("#main", "I wonder what scout would say about scouting");

        Assert.Null(await WaitForAsync(human, m => m.Text == "from-scout", TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AStreamedHandOffReachesTheAgentNamed()
    {
        // Reported from a real room: the delegator, asked to bring scout in, wrote "@scout ..."
        // and scout never answered. An agent's reply goes out as a stream, and agents listened
        // only for plain messages - so people were heard and other agents never were. The
        // routing tests pass because a routed hand-off is a plain message; this is the model
        // speaking for itself, which is how a delegator actually talks to another agent.
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(Options("local-a", AgentLocality.Local), "@scout what do you make of this?");
        await delegator.StartAsync(_transport);
        await using var scout = new EchoAgent(Options("scout", AgentLocality.Local), "from-scout");
        await scout.StartAsync(_transport);

        await WaitForDelegatorAsync(human, "local-a");

        await human.SendMessageAsync("#main", "ask scout about it");

        Assert.NotNull(await WaitForAsync(human, m => m.Text == "from-scout"));
        Assert.Contains("what do you make of this", scout.Answered.Single(), StringComparison.Ordinal);

        // And the delegator did not take its own streamed words for somebody else's.
        Assert.Single(delegator.Answered);
    }

    [Fact]
    public async Task NamingANumberedAgentDoesNotSummonTheOneItIsNumberedAfter()
    {
        // Found on a release run: "@local-2 get started" also started a turn in "local", because
        // a mention ended at the first character that was not a letter or digit. The turn it took
        // held its one-at-a-time gate, and the next message actually meant for it was dropped.
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(Options("local-a", AgentLocality.Local), "from-the-delegator");
        await delegator.StartAsync(_transport);
        await using var scout = new EchoAgent(Options("scout", AgentLocality.Local), "from-scout");
        await scout.StartAsync(_transport);
        await using var second = new EchoAgent(Options("scout-2", AgentLocality.Local), "from-scout-2");
        await second.StartAsync(_transport);

        await WaitForDelegatorAsync(human, "local-a");

        await human.SendMessageAsync("#main", "@scout-2 what do you make of this?");

        Assert.NotNull(await WaitForAsync(human, m => m.Text == "from-scout-2"));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Empty(scout.Answered);
    }

    [Theory]
    [InlineData("@scout what now", true)]
    [InlineData("ask @scout.", true)]
    [InlineData("@scout, @third: both of you", true)]
    [InlineData("@scout-2 and @scout", true)]
    [InlineData("@scouting the area", false)]
    [InlineData("@scout-2 what now", false)]
    [InlineData("@scout_b what now", false)]
    [InlineData("@scout.b what now", false)]
    [InlineData("scout, what now", false)]
    public void AMentionEndsWhereTheNickDoes(string text, bool namesScout) =>
        Assert.Equal(namesScout, Mentions.Names(text, "scout"));

    [Fact]
    public async Task AThirdPartyAgentAnnouncesTheEgressBeforeAnswering()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(Options("local-a", AgentLocality.Local), "from-the-delegator");
        await delegator.StartAsync(_transport);
        await using var frontier = new EchoAgent(
            Options("scout", AgentLocality.Frontier, DataSensitivity.Public), "from-scout");
        await frontier.StartAsync(_transport);

        await WaitForDelegatorAsync(human, "local-a");

        await human.SendMessageAsync("#main", "@scout what is the capital of France?");

        // The delegator would have announced this before routing; a mention skips the delegator,
        // so the agent has to say it for itself or the room never learns data left.
        Assert.NotNull(await WaitForAsync(human, m => m.Text.StartsWith("[egress]", StringComparison.Ordinal)));
        Assert.NotNull(await WaitForAsync(human, m => m.Text == "from-scout"));
    }

    [Fact]
    public async Task AThirdPartyAgentDeclinesWhatItIsNotClearedFor()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var delegator = new EchoAgent(Options("local-a", AgentLocality.Local), "from-the-delegator");
        await delegator.StartAsync(_transport);
        await using var frontier = new EchoAgent(
            Options("scout", AgentLocality.Frontier, DataSensitivity.Public), "from-scout");
        await frontier.StartAsync(_transport);

        await WaitForDelegatorAsync(human, "local-a");

        // This is the whole point of the safeguard: without it, the clearance model comes undone
        // by typing an "@" in front of a name.
        await human.SendMessageAsync("#main", "@scout here is the customer password, please review");

        Assert.NotNull(await WaitForAsync(human, m => m.Text.Contains("not taking that one", StringComparison.Ordinal)));
        Assert.Null(await WaitForAsync(human, m => m.Text == "from-scout", TimeSpan.FromSeconds(2)));
        Assert.Empty(frontier.Answered);
    }
}
