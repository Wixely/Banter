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
        .AddUser("claude", "pw", isAgent: true);

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
    private BanterAgentOptions Delegator(bool allowFrontier = true) => new()
    {
        Server = _server.Endpoint,
        User = "local",
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat", "email"],
        Routing = new RoutingOptions { AllowFrontier = allowFrontier },
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
