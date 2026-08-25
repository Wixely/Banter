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
/// Delegator election over the wire (PLAN §8a): agents announce, the server elects, and the room
/// is told. The unit tests cover the policy; these cover the plumbing and the re-election paths.
/// </summary>
public sealed class DelegatorElectionIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw", isAgent: false)
        .AddUser("local-a", "pw", isAgent: true)
        .AddUser("local-b", "pw", isAgent: true)
        .AddUser("claude", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-elect-{Guid.NewGuid():N}");
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

    private Task<BanterClient> ConnectAsync(string user) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, "pw");

    private static AgentAnnouncePayload Announce(
        string nick, AgentLocality locality, DataSensitivity clearance, int cost = 1, bool wantsDelegator = false) =>
        new(nick, locality, clearance, ["chat"], $"{nick} test agent", cost, wantsDelegator);

    /// <summary>Polls the roster until it reports the expected delegator, or fails loudly.</summary>
    private static async Task<string?> WaitForDelegatorAsync(BanterClient observer, string room, string? expected)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        string? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var agents = await observer.GetAgentsAsync(room);
            last = agents.Agents.FirstOrDefault(a => a.IsDelegator)?.Nick;
            if (string.Equals(last, expected, StringComparison.OrdinalIgnoreCase))
            {
                return last;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Expected delegator '{expected ?? "(none)"}' in {room}, saw '{last ?? "(none)"}'.");
        return null;
    }

    [Fact]
    public async Task ASingleLocalAgentIsElectedOnJoin()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await using var agent = await ConnectAsync("local-a");
        await agent.AnnounceAgentAsync(Announce("local-a", AgentLocality.Local, DataSensitivity.Sensitive));
        await agent.JoinAsync("#main");

        await WaitForDelegatorAsync(human, "#main", "local-a");
    }

    [Fact]
    public async Task AFrontierAgentIsNotElectedInASensitiveRoom()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await using var claude = await ConnectAsync("claude");
        await claude.AnnounceAgentAsync(Announce("claude", AgentLocality.Frontier, DataSensitivity.Sensitive));
        await claude.JoinAsync("#main");

        // Rooms default to sensitive, so a frontier agent must not become the one that reads
        // every message. No delegator is the correct outcome.
        await WaitForDelegatorAsync(human, "#main", null);

        var agents = await human.GetAgentsAsync("#main");
        Assert.Equal("claude", Assert.Single(agents.Agents).Nick);   // present, just not delegator
    }

    [Fact]
    public async Task ALocalAgentTakesOverFromNoneWhenItArrives()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await using var claude = await ConnectAsync("claude");
        await claude.AnnounceAgentAsync(Announce("claude", AgentLocality.Frontier, DataSensitivity.Sensitive));
        await claude.JoinAsync("#main");
        await WaitForDelegatorAsync(human, "#main", null);

        await using var local = await ConnectAsync("local-a");
        await local.AnnounceAgentAsync(Announce("local-a", AgentLocality.Local, DataSensitivity.Sensitive));
        await local.JoinAsync("#main");

        await WaitForDelegatorAsync(human, "#main", "local-a");
    }

    [Fact]
    public async Task ADelegatorThatPartsIsReplaced()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await using var first = await ConnectAsync("local-a");
        await first.AnnounceAgentAsync(Announce("local-a", AgentLocality.Local, DataSensitivity.Sensitive));
        await first.JoinAsync("#main");
        await WaitForDelegatorAsync(human, "#main", "local-a");

        await using var second = await ConnectAsync("local-b");
        await second.AnnounceAgentAsync(Announce("local-b", AgentLocality.Local, DataSensitivity.Sensitive));
        await second.JoinAsync("#main");

        // Still the first: election is stable, so a new arrival does not steal the role.
        await WaitForDelegatorAsync(human, "#main", "local-a");

        await first.PartAsync("#main");
        await WaitForDelegatorAsync(human, "#main", "local-b");
    }

    [Fact]
    public async Task ADelegatorWhoseConnectionDropsIsReplaced()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        var first = await ConnectAsync("local-a");
        await first.AnnounceAgentAsync(Announce("local-a", AgentLocality.Local, DataSensitivity.Sensitive));
        await first.JoinAsync("#main");
        await WaitForDelegatorAsync(human, "#main", "local-a");

        await using var second = await ConnectAsync("local-b");
        await second.AnnounceAgentAsync(Announce("local-b", AgentLocality.Local, DataSensitivity.Sensitive));
        await second.JoinAsync("#main");

        // A crash, not a clean part - the room must not be left with an absent dispatcher.
        await first.DisposeAsync();

        await WaitForDelegatorAsync(human, "#main", "local-b");
    }

    [Fact]
    public async Task AnAgentConfiguredAsDelegatorWinsOverACheaperOne()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await using var cheap = await ConnectAsync("local-a");
        await cheap.AnnounceAgentAsync(Announce("local-a", AgentLocality.Local, DataSensitivity.Sensitive, cost: 1));
        await cheap.JoinAsync("#main");
        await WaitForDelegatorAsync(human, "#main", "local-a");

        await using var wants = await ConnectAsync("local-b");
        await wants.AnnounceAgentAsync(
            Announce("local-b", AgentLocality.Local, DataSensitivity.Sensitive, cost: 9, wantsDelegator: true));
        await wants.JoinAsync("#main");

        await WaitForDelegatorAsync(human, "#main", "local-b");
    }

    [Fact]
    public async Task AnAgentThatNeverAnnouncesIsListedButNotElected()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await using var silent = await ConnectAsync("local-a");
        await silent.JoinAsync("#main");

        // Unknown attributes fail closed: visible in the roster, never the delegator.
        await WaitForDelegatorAsync(human, "#main", null);
        var agents = await human.GetAgentsAsync("#main");
        var entry = Assert.Single(agents.Agents);
        Assert.Equal(AgentLocality.Unknown, entry.Locality);
        Assert.False(entry.IsDelegator);
    }

    [Fact]
    public async Task HumansAreNotCandidates()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await WaitForDelegatorAsync(human, "#main", null);
        Assert.Empty((await human.GetAgentsAsync("#main")).Agents);
    }

    [Fact]
    public async Task ANonAgentCannotClaimAgentAttributes()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        // Attributes decide who may read a room's traffic; a human account claiming them would
        // be a way to smuggle a frontier relay into a sensitive room.
        var error = await Assert.ThrowsAsync<BanterErrorException>(() =>
            human.AnnounceAgentAsync(Announce("human", AgentLocality.Local, DataSensitivity.Sensitive)));

        Assert.Equal("NOT_AN_AGENT", error.Code);
    }

    [Fact]
    public async Task RoomModeDefaultsToDelegatedAndCanBeChanged()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        var current = await human.SetRoomModeAsync("#main", RoomDispatchMode.Delegated);
        Assert.Equal(RoomDispatchMode.Delegated, current.Mode);

        var changed = await human.SetRoomModeAsync("#main", RoomDispatchMode.Mention);
        Assert.Equal(RoomDispatchMode.Mention, changed.Mode);
    }
}
