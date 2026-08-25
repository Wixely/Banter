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
/// Sub-rooms (PLAN §8a): the delegator's side channel for a piece of work. The rules that matter
/// are the ones stopping a sub-room from becoming a way around the egress policy.
/// </summary>
public sealed class SubRoomTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw")
        .AddUser("local", "pw", isAgent: true)
        .AddUser("local-2", "pw", isAgent: true)
        .AddUser("claude", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-subroom-{Guid.NewGuid():N}");
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
        string nick, AgentLocality locality, DataSensitivity clearance, params string[] skills) =>
        new(nick, locality, clearance, skills.Length > 0 ? skills : ["chat"], "", 1, false);

    private static async Task WaitForDelegatorAsync(BanterClient observer, string room, string expected)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var agents = await observer.GetAgentsAsync(room);
            if (agents.Agents.Any(a => a.IsDelegator && a.Nick == expected))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"'{expected}' never became delegator of {room}.");
    }

    [Fact]
    public async Task ADelegatorCanOpenASubRoomAndPullAClearedAgentIn()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await using var local = await ConnectAsync("local");
        await local.AnnounceAgentAsync(Announce("local", AgentLocality.Local, DataSensitivity.Sensitive));
        await local.JoinAsync("#main");
        await WaitForDelegatorAsync(human, "#main", "local");

        await using var helper = await ConnectAsync("local-2");
        await helper.AnnounceAgentAsync(Announce("local-2", AgentLocality.Local, DataSensitivity.Sensitive, "code"));
        await helper.JoinAsync("#main");

        var created = await local.CreateRoomAsync("#task-1", parentRoom: "#main", purpose: "fix the parser");
        Assert.Equal("#task-1", created.Room);
        Assert.Equal("#main", created.ParentRoom);

        await WaitForDelegatorAsync(local, "#task-1", "local");
        await local.MoveAgentAsync("local-2", "#task-1", "needs the code skill");

        var members = await local.GetMembersAsync("#task-1");
        Assert.Contains(members.Members, m => m.Nick == "local-2");
    }

    [Fact]
    public async Task ASubRoomInheritsItsParentsSensitivitySoItCannotBeUsedToLaunderData()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await using var local = await ConnectAsync("local");
        await local.AnnounceAgentAsync(Announce("local", AgentLocality.Local, DataSensitivity.Sensitive));
        await local.JoinAsync("#main");
        await WaitForDelegatorAsync(human, "#main", "local");

        await using var claude = await ConnectAsync("claude");
        await claude.AnnounceAgentAsync(Announce("claude", AgentLocality.Frontier, DataSensitivity.Public, "web"));
        await claude.JoinAsync("#main");

        await local.CreateRoomAsync("#task-2", parentRoom: "#main", purpose: "research");
        await WaitForDelegatorAsync(local, "#task-2", "local");

        // The whole point: opening a child room must not create somewhere a frontier agent may
        // read what the parent room was protecting.
        var error = await Assert.ThrowsAsync<BanterErrorException>(() =>
            local.MoveAgentAsync("claude", "#task-2"));

        Assert.Equal("NOT_CLEARED", error.Code);
    }

    [Fact]
    public async Task OpeningASubRoomIsAnnouncedInTheParent()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await using var local = await ConnectAsync("local");
        await local.AnnounceAgentAsync(Announce("local", AgentLocality.Local, DataSensitivity.Sensitive));
        await local.JoinAsync("#main");
        await WaitForDelegatorAsync(human, "#main", "local");

        await local.CreateRoomAsync("#task-3", parentRoom: "#main", purpose: "chase the flaky test");

        // A side channel the humans cannot see from the main room is a side channel they cannot
        // audit, so the link is announced where the conversation started.
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var history = await human.GetHistoryAsync("#main", limit: 100);
            if (history.Messages.Any(m => m.Text.Contains("#task-3") && m.Text.Contains("chase the flaky test")))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Opening a sub-room was not announced in the parent room.");
    }

    [Fact]
    public async Task OnlyTheDelegatorMayMoveAgentsIntoARoom()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await using var local = await ConnectAsync("local");
        await local.AnnounceAgentAsync(Announce("local", AgentLocality.Local, DataSensitivity.Sensitive));
        await local.JoinAsync("#main");
        await WaitForDelegatorAsync(human, "#main", "local");

        await using var helper = await ConnectAsync("local-2");
        await helper.AnnounceAgentAsync(Announce("local-2", AgentLocality.Local, DataSensitivity.Sensitive));
        await helper.JoinAsync("#main");

        // A non-delegator agent cannot conscript another agent into a room.
        var error = await Assert.ThrowsAsync<BanterErrorException>(() =>
            helper.MoveAgentAsync("local", "#main"));

        Assert.Equal("NOT_DELEGATOR", error.Code);
    }

    [Fact]
    public async Task HumansCannotBeMovedBetweenRooms()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        await using var local = await ConnectAsync("local");
        await local.AnnounceAgentAsync(Announce("local", AgentLocality.Local, DataSensitivity.Sensitive));
        await local.JoinAsync("#main");
        await WaitForDelegatorAsync(human, "#main", "local");

        await local.CreateRoomAsync("#task-4", parentRoom: "#main");
        await WaitForDelegatorAsync(local, "#task-4", "local");

        // An agent dragging a person into a room is not a thing this protocol does.
        var error = await Assert.ThrowsAsync<BanterErrorException>(() =>
            local.MoveAgentAsync("human", "#task-4"));

        Assert.Equal("NOT_AN_AGENT", error.Code);
    }

    [Fact]
    public async Task ARoomOpenedWithNoParentIsAnOrdinaryRoom()
    {
        await using var human = await ConnectAsync("human");

        var created = await human.CreateRoomAsync("#standalone");

        Assert.Equal("#standalone", created.Room);
        Assert.Null(created.ParentRoom);

        var members = await human.GetMembersAsync("#standalone");
        Assert.Contains(members.Members, m => m.Nick == "human");
    }
}
