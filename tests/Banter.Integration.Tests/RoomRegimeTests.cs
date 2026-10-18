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
/// A room that had a local delegator keeps the seat for a local agent when that one leaves, until
/// a local agent takes it again or an admin says a third-party delegator is intended.
/// </summary>
public sealed class RoomRegimeTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw", isAgent: false, isAdmin: true)
        .AddUser("member", "pw")
        .AddUser("local", "pw", isAgent: true)
        .AddUser("claude", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    private sealed class QuietAgent(BanterAgentOptions options) : BanterAgent(options)
    {
        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return "ok";
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-regime-{Guid.NewGuid():N}");
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

    private BanterAgentOptions Local() => new()
    {
        Server = _server.Endpoint,
        User = "local",
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat"],
    };

    /// <summary>A third-party agent that asks for the seat - which the configured-delegator rule
    /// grants outright, ahead of every eligibility check. The case the regime exists for.</summary>
    private BanterAgentOptions FrontierWantingTheSeat() => new()
    {
        Server = _server.Endpoint,
        User = "claude",
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Frontier,
        Clearance = DataSensitivity.Public,
        Skills = ["web"],
        WantsDelegator = true,
    };

    private static async Task<string?> DelegatorAsync(BanterClient watcher) =>
        (await watcher.GetAgentsAsync("#main")).Agents.FirstOrDefault(a => a.IsDelegator)?.Nick;

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

    [Fact]
    public async Task WhenTheLocalDelegatorLeavesTheSeatIsHeldUntilAnAdminSaysOtherwise()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        var regimes = new List<RoomRegimePayload>();
        human.RoomRegimeChanged += r =>
        {
            lock (regimes)
            {
                regimes.Add(r);
            }
        };
        await human.JoinAsync("#main");

        var local = new QuietAgent(Local());
        await local.StartAsync(_transport);
        await UntilAsync(async () => await DelegatorAsync(human) == "local", "local never took the seat");

        // The local delegator goes.
        await local.DisposeAsync();
        await UntilAsync(
            () => Task.FromResult(regimes.Any(r => r.Regime == RoomRegime.Interregnum)),
            "the room never went into interregnum");
        Assert.Contains("local", regimes.Last(r => r.Regime == RoomRegime.Interregnum).Reason);

        // A third-party agent that wants the seat arrives, and does not get it.
        await using var claude = new QuietAgent(FrontierWantingTheSeat());
        await claude.StartAsync(_transport);
        await UntilAsync(
            async () => (await human.GetAgentsAsync("#main")).Agents.Any(a => a.Nick == "claude"),
            "claude never joined");
        Assert.Null(await DelegatorAsync(human));

        // An admin says that is intended, and the usual rules apply again.
        var ended = await human.SetRoomRegimeAsync("#main", RoomRegime.Republic);
        Assert.Equal(RoomRegime.Republic, ended.Regime);
        await UntilAsync(async () => await DelegatorAsync(human) == "claude", "claude was never elected");
    }

    [Fact]
    public async Task ALocalAgentTakingTheSeatEndsTheInterregnum()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        var regimes = new List<RoomRegime>();
        human.RoomRegimeChanged += r =>
        {
            lock (regimes)
            {
                regimes.Add(r.Regime);
            }
        };
        await human.JoinAsync("#main");

        var first = new QuietAgent(Local());
        await first.StartAsync(_transport);
        await UntilAsync(async () => await DelegatorAsync(human) == "local", "local never took the seat");
        await first.DisposeAsync();
        await UntilAsync(() => Task.FromResult(regimes.Contains(RoomRegime.Interregnum)), "no interregnum");

        // The same local agent back - a dropped connection, say.
        await using var again = new QuietAgent(Local());
        await again.StartAsync(_transport);
        await UntilAsync(async () => await DelegatorAsync(human) == "local", "local never took the seat back");
        await UntilAsync(() => Task.FromResult(regimes[^1] == RoomRegime.Republic), "the interregnum never ended");
    }

    [Fact]
    public async Task OnlyAnAdminMayChangeARoomsRegime()
    {
        await using var member = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "member", "pw");
        await member.JoinAsync("#main");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(
            () => member.SetRoomRegimeAsync("#main", RoomRegime.Interregnum));
        Assert.Equal("NOT_ADMIN", refused.Code);

        // Asking for the regime it already has is a read, and anybody may read.
        Assert.Equal(RoomRegime.Republic, (await member.SetRoomRegimeAsync("#main", RoomRegime.Republic)).Regime);
    }
}
