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
/// The delegator routing a request to the right agent, over the wire (PLAN §8a) — including the
/// egress announcement that has to happen before anything leaves.
/// </summary>
public sealed class DelegatorRoutingTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw")
        .AddUser("local", "pw", isAgent: true)
        .AddUser("claude", "pw", isAgent: true)
        .AddUser("scout", "pw", isAgent: true)
        .AddUser("local-2", "pw", isAgent: true)
        .AddUser("local-3", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    private sealed class MarkerAgent(BanterAgentOptions options, string reply) : BanterAgent(options)
    {
        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return reply;
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-routing-{Guid.NewGuid():N}");
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

    /// <summary>The local delegator: routes, and is cleared for everything.</summary>
    private BanterAgentOptions Delegator(
        bool allowFrontier = true, bool subRooms = false,
        AgentWorkMode workMode = AgentWorkMode.DelegateAndWork) => new()
    {
        Server = _server.Endpoint,
        User = "local",
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat", "email"],
        WorkMode = workMode,
        Routing = new RoutingOptions { AllowFrontier = allowFrontier, SubRoomForFanOut = subRooms },
    };

    /// <summary>A local helper, for fan-outs that stay in-house.</summary>
    private BanterAgentOptions LocalHelper(string nick, string skill) => new()
    {
        Server = _server.Endpoint,
        User = nick,
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = [skill, "chat"],
        CostTier = 2,
    };

    /// <summary>A second researcher, so a fan-out has more than one recipient: the delegator
    /// excludes itself from its own routing, so two agents in a room is not a fan-out.</summary>
    private BanterAgentOptions SecondResearcher() => new()
    {
        Server = _server.Endpoint,
        User = "scout",
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Frontier,
        Clearance = DataSensitivity.Public,
        Skills = ["github", "web", "research"],
        CostTier = 6,
    };

    /// <summary>The frontier specialist: only cleared for public data.</summary>
    private BanterAgentOptions Frontier() => new()
    {
        Server = _server.Endpoint,
        User = "claude",
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Frontier,
        Clearance = DataSensitivity.Public,
        Skills = ["github", "web", "research"],
        CostTier = 5,
    };

    // ── What a delegator does with work nobody else can take (PLAN 8a) ───────────────────────

    [Fact]
    public async Task ADelegateOnlyAgentHandsWorkOutAndAnswersNothingItself()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(
            Delegator(workMode: AgentWorkMode.DelegateOnly), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        // Alone in the room and asked something only it could take. Delegate-only means it does
        // not: answering would hold its turn, and a delegator mid-answer cannot route.
        await human.SendMessageAsync("#main", "write me a short note about the meeting");

        var refusal = await WaitForAsync(human, m => m.Sender == "local" && m.Text.Contains("hand work out"));
        Assert.NotNull(refusal);

        // And it really did not answer — the refusal is not accompanied by the answer as well.
        Assert.Null(await WaitForAsync(
            human, m => m.Text == "LOCAL-ANSWERED", within: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task WorkWhenAloneAnswersOnlyWithNobodyToHandItTo()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(
            Delegator(workMode: AgentWorkMode.WorkWhenAlone), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        // Alone: it answers, because a room with one agent in it still has to work.
        await human.SendMessageAsync("#main", "write me a short note about the meeting");
        Assert.NotNull(await WaitForAsync(human, m => m.Text == "LOCAL-ANSWERED"));
    }

    [Fact]
    public async Task ADelegatorThatOnlyDelegatesStaysFreeWhileAWorkingOneIsBusy()
    {
        // The point of the whole setting. A delegator that answers holds its turn gate for the
        // length of the answer, so the request arriving behind it waits; one that only delegates
        // is never mid-answer and picks the second request up immediately.
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(
            Delegator(workMode: AgentWorkMode.DelegateOnly), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await using var claude = new MarkerAgent(Frontier(), "FRONTIER-ANSWERED");
        await claude.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        // One it cannot hand out, immediately followed by one it can.
        await human.SendMessageAsync("#main", "write me a short note about the meeting");
        await human.SendMessageAsync("#main", "search github for the public issue about parsing");

        // The second still gets routed: refusing the first cost it nothing.
        Assert.NotNull(await WaitForAsync(human, m => m.Text == "FRONTIER-ANSWERED"));
    }

    private static async Task<MsgPayload?> WaitForAsync(
        BanterClient client, Func<MsgPayload, bool> predicate, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? Timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var history = await client.GetHistoryAsync("#main", limit: 300);
            var hit = history.Messages.FirstOrDefault(predicate);
            if (hit is not null)
            {
                return hit;
            }

            await Task.Delay(25);
        }

        return null;
    }

    private async Task<BanterClient> ReadyRoomAsync()
    {
        var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        return human;
    }

    private static async Task WaitForDelegatorAsync(BanterClient observer, string expected)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var agents = await observer.GetAgentsAsync("#main");
            if (agents.Agents.Any(a => a.IsDelegator && a.Nick == expected))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"'{expected}' never became delegator.");
    }

    [Fact]
    public async Task APublicSkillMatchIsRoutedToTheFrontierAgentWithAnEgressAnnouncement()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(Delegator(), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await using var claude = new MarkerAgent(Frontier(), "FRONTIER-ANSWERED");
        await claude.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        await human.SendMessageAsync("#main", "search github for the public issue about parsing");

        // The egress notice must exist, and must name the agent and the classification.
        var egress = await WaitForAsync(human, m => m.Sender == "local" && m.Text.StartsWith("[egress]"));
        Assert.NotNull(egress);
        Assert.Contains("claude", egress.Text);
        Assert.Contains("public", egress.Text);

        Assert.NotNull(await WaitForAsync(human, m => m.Text == "FRONTIER-ANSWERED"));
    }

    [Fact]
    public async Task ASensitiveRequestStaysLocalAndAnnouncesNoEgress()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(Delegator(), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await using var claude = new MarkerAgent(Frontier(), "FRONTIER-ANSWERED");
        await claude.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        await human.SendMessageAsync("#main", "summarise my email inbox for today");

        Assert.NotNull(await WaitForAsync(human, m => m.Text == "LOCAL-ANSWERED"));

        // Nothing left our systems, and nothing claimed to.
        Assert.Null(await WaitForAsync(
            human, m => m.Text.StartsWith("[egress]"), within: TimeSpan.FromSeconds(2)));
        Assert.Null(await WaitForAsync(
            human, m => m.Text == "FRONTIER-ANSWERED", within: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AnUnrecognisedRequestStaysLocal()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(Delegator(), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await using var claude = new MarkerAgent(Frontier(), "FRONTIER-ANSWERED");
        await claude.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        // Nothing marks this as public, so it must not leave - the fail-closed default.
        await human.SendMessageAsync("#main", "deal with the Thompson matter please");

        Assert.NotNull(await WaitForAsync(human, m => m.Text == "LOCAL-ANSWERED"));
        Assert.Null(await WaitForAsync(
            human, m => m.Text.StartsWith("[egress]"), within: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task StaticPolicyBlocksFrontierRoutingEvenForAPublicRequest()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(Delegator(allowFrontier: false), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await using var claude = new MarkerAgent(Frontier(), "FRONTIER-ANSWERED");
        await claude.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        await human.SendMessageAsync("#main", "search github for the public issue about parsing");

        // Classified public and skill-matched to claude, but policy wins.
        Assert.NotNull(await WaitForAsync(human, m => m.Text == "LOCAL-ANSWERED"));
        Assert.Null(await WaitForAsync(
            human, m => m.Text == "FRONTIER-ANSWERED", within: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AskingEveryoneFansOutToAllEligibleAgents()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(Delegator(), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await using var claude = new MarkerAgent(Frontier(), "FRONTIER-ANSWERED");
        await claude.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        // Public, so both are eligible, and the phrase asks for more than one answer.
        await human.SendMessageAsync("#main", "what does everyone think about this public github issue");

        Assert.NotNull(await WaitForAsync(human, m => m.Text == "FRONTIER-ANSWERED"));

        // The egress notice must name the frontier recipient even in a fan-out.
        var egress = await WaitForAsync(human, m => m.Text.StartsWith("[egress]"));
        Assert.NotNull(egress);
        Assert.Contains("claude", egress.Text);
    }

    [Fact]
    public async Task FanningOutStillExcludesAgentsThatAreNotCleared()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(Delegator(), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await using var claude = new MarkerAgent(Frontier(), "FRONTIER-ANSWERED");
        await claude.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        // Sensitive: asking everyone must not widen who may see it.
        await human.SendMessageAsync("#main", "what does everyone think about my email inbox problem");

        Assert.Null(await WaitForAsync(
            human, m => m.Text == "FRONTIER-ANSWERED", within: TimeSpan.FromSeconds(3)));
        Assert.Null(await WaitForAsync(
            human, m => m.Text.StartsWith("[egress]"), within: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ALocalFanOutCanBeTakenIntoASubRoom()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(Delegator(subRooms: true), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await using var helperA = new MarkerAgent(LocalHelper("local-2", "chat"), "HELPER-A");
        await helperA.StartAsync(_transport);
        await using var helperB = new MarkerAgent(LocalHelper("local-3", "chat"), "HELPER-B");
        await helperB.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        await human.SendMessageAsync("#main", "what does everyone think about the Thompson matter");

        // The parent room must say where the conversation went - a side channel the humans
        // cannot find is one they cannot follow.
        // The room is named after the work, not the parent: a room list of identifiers is
        // unreadable, a room list of things being done is a status board.
        var pointer = await WaitForAsync(human, m => m.Text.StartsWith("Taking this to #"));
        if (pointer is null)
        {
            var dump = await human.GetHistoryAsync("#main", limit: 100);
            Assert.Fail("no sub-room pointer. Room said: " +
                string.Join(" | ", dump.Messages.Select(m => $"<{m.Sender}> {m.Text}")));
        }

        var subRoom = pointer.Text.Split(' ')[3];
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var members = await human.GetMembersAsync(subRoom);
            if (members.Members.Any(m => m.Nick == "local-2") && members.Members.Any(m => m.Nick == "local-3"))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Agents were never moved into {subRoom}.");
    }

    [Fact]
    public async Task AFanOutInvolvingAThirdPartyStaysInTheMainRoom()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(Delegator(subRooms: true), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await using var claude = new MarkerAgent(Frontier(), "FRONTIER-ANSWERED");
        await claude.StartAsync(_transport);
        await using var scout = new MarkerAgent(SecondResearcher(), "SCOUT-ANSWERED");
        await scout.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        await human.SendMessageAsync("#main", "what does everyone think about this public github issue");

        // Both third parties answer, and they answer here: moving the one exchange that leaves
        // our systems into a side channel would make the most consequential thing the least
        // visible.
        Assert.NotNull(await WaitForAsync(human, m => m.Text == "FRONTIER-ANSWERED"));
        Assert.NotNull(await WaitForAsync(human, m => m.Text.StartsWith("[egress]")));
        Assert.Null(await WaitForAsync(
            human, m => m.Text.StartsWith("Taking this to"), within: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ASensitiveFanOutCannotBeLaunderedThroughASubRoom()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(Delegator(subRooms: true), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await using var claude = new MarkerAgent(Frontier(), "FRONTIER-ANSWERED");
        await claude.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        // Sensitive, so claude is not eligible and there is nothing to fan out to. No side room
        // should appear, and nothing should leave.
        await human.SendMessageAsync("#main", "what does everyone think about my email inbox");

        Assert.NotNull(await WaitForAsync(human, m => m.Text == "LOCAL-ANSWERED"));
        Assert.Null(await WaitForAsync(
            human, m => m.Text.StartsWith("Taking this to"), within: TimeSpan.FromSeconds(2)));
        Assert.Null(await WaitForAsync(
            human, m => m.Text.StartsWith("[egress]"), within: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ADelegatorAloneAnswersItselfRatherThanStalling()
    {
        await using var human = await ReadyRoomAsync();
        await using var local = new MarkerAgent(Delegator(), "LOCAL-ANSWERED");
        await local.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "local");

        await human.SendMessageAsync("#main", "search github for something public");

        Assert.NotNull(await WaitForAsync(human, m => m.Text == "LOCAL-ANSWERED"));
    }
}
