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
