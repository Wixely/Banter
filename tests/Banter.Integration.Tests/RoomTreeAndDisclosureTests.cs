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
/// Rooms sit in a tree that can be rearranged and survives a restart, and each room says who may
/// draw on what was said in it from elsewhere - checked by the server on every recall.
/// </summary>
public sealed class RoomTreeAndDisclosureTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("admin", "pw", isAgent: false, isAdmin: true)
        .AddUser("member", "pw")
        .AddUser("boss", "pw", isAgent: true)
        .AddUser("local", "pw", isAgent: true)
        .AddUser("claude", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private FileStore _files = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-tree-{Guid.NewGuid():N}");
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

    private async Task<BanterClient> PersonAsync(string nick, params string[] rooms)
    {
        var client = await BanterClient.ConnectAsync(_transport, _server.Endpoint, nick, "pw");
        foreach (var room in rooms)
        {
            await client.JoinAsync(room);
        }

        return client;
    }

    /// <summary>A bare agent session that has said what it is, so clearance can be judged.</summary>
    private async Task<BanterClient> AgentAsync(string nick, bool local = true, params string[] rooms)
    {
        var client = await BanterClient.ConnectAsync(_transport, _server.Endpoint, nick, "pw");
        await client.AnnounceAgentAsync(new AgentAnnouncePayload(
            nick,
            local ? AgentLocality.Local : AgentLocality.Frontier,
            local ? DataSensitivity.Sensitive : DataSensitivity.Public,
            ["chat"]));
        foreach (var room in rooms)
        {
            await client.JoinAsync(room);
        }

        return client;
    }

    private static async Task<RoomSummary> RoomAsync(BanterClient c, string room) =>
        (await c.ListRoomsAsync()).Rooms.Single(r => r.Name == room);

    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (BanterClientException)
            {
                // Mid-reconnect; ask again.
            }

            await Task.Delay(50);
        }

        Assert.Fail(what);
    }

    // ── The tree ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheTreeCanBeRearrangedAndSurvivesARestart()
    {
        var port = _server.Endpoint.Port;
        await using var admin = await PersonAsync("admin", "#main");
        await admin.CreateRoomAsync("#a", "#main");
        await admin.CreateRoomAsync("#b", "#main");

        await admin.MoveRoomAsync("#b", "#a");
        await admin.MoveRoomAsync("#a", null);
        Assert.Null((await RoomAsync(admin, "#a")).ParentRoom);
        Assert.Equal("#a", (await RoomAsync(admin, "#b")).ParentRoom);

        // A room cannot go inside itself, however far down.
        var cycle = await Assert.ThrowsAsync<BanterErrorException>(() => admin.MoveRoomAsync("#a", "#b"));
        Assert.Equal("BAD_MOVE", cycle.Code);

        var back = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        admin.Reconnected += () => back.TrySetResult();
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

        await back.Task.WaitAsync(Patience);
        await UntilAsync(async () => (await RoomAsync(admin, "#b")).ParentRoom == "#a", "the tree was flattened by the restart");
    }

    [Fact]
    public async Task OnlyAnAdminOrTheMainDelegatorMovesRooms()
    {
        await using var admin = await PersonAsync("admin", "#main");
        await using var member = await PersonAsync("member", "#main");
        await admin.CreateRoomAsync("#a");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(() => member.MoveRoomAsync("#a", "#main"));
        Assert.Equal("NOT_ALLOWED", refused.Code);

        // The main room's delegator - the only agent there, local and cleared, so elected.
        await using var boss = await AgentAsync("boss", local: true, "#main");
        await UntilAsync(async () => (await admin.GetAgentsAsync("#main")).Agents.Any(a => a.Nick == "boss" && a.IsDelegator),
            "boss never ran #main");
        await boss.MoveRoomAsync("#a", "#main");
        Assert.Equal("#main", (await RoomAsync(admin, "#a")).ParentRoom);
    }

    [Fact]
    public async Task ASideRoomTakesItsParentsDisclosure()
    {
        await using var admin = await PersonAsync("admin", "#main");
        await admin.CreateRoomAsync("#vault");
        await admin.SetRoomDisclosureAsync("#vault", RoomDisclosure.Isolated);
        await admin.JoinAsync("#vault");
        await admin.CreateRoomAsync("#vault-work", "#vault");

        Assert.Equal(RoomDisclosure.Isolated, (await RoomAsync(admin, "#vault-work")).Disclosure);
    }

    [Fact]
    public async Task OnlyTheRoomsDelegatorTheMainDelegatorOrAnAdminSetsDisclosure()
    {
        await using var admin = await PersonAsync("admin", "#main");
        await using var member = await PersonAsync("member", "#main");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(
            () => member.SetRoomDisclosureAsync("#main", RoomDisclosure.Global));
        Assert.Equal("NOT_ALLOWED", refused.Code);

        Assert.Equal(RoomDisclosure.Global, (await admin.SetRoomDisclosureAsync("#main", RoomDisclosure.Global)).Disclosure);
        Assert.Equal(RoomDisclosure.Global, (await RoomAsync(admin, "#main")).Disclosure);
    }

    // ── Recall, room by room ──────────────────────────────────────────────────────────────────

    /// <summary>A room with something said in it, and an agent that may or may not be able to read it.</summary>
    private async Task<BanterClient> RoomWithASecretAsync(string room, RoomDisclosure disclosure, string? parent = null)
    {
        var admin = await PersonAsync("admin", "#main");
        if (parent is not null)
        {
            await admin.JoinAsync(parent);
        }

        await admin.CreateRoomAsync(room, parent);
        await admin.SetRoomDisclosureAsync(room, disclosure);
        await admin.SendMessageAsync(room, "the launch date is the 3rd");
        await UntilAsync(async () => (await admin.GetHistoryAsync(room, limit: 5)).Messages.Count > 0, "nothing was said");
        return admin;
    }

    [Fact]
    public async Task MembersAnAgentInTheRoomMayDrawOnItFromElsewhereAndNobodyElseMay()
    {
        await using var admin = await RoomWithASecretAsync("#plans", RoomDisclosure.Members);
        await using var inside = await AgentAsync("local", true, "#main", "#plans");
        await using var outside = await AgentAsync("boss", true, "#main");

        var page = await inside.RecallRoomAsync("#plans", "#main");
        Assert.Contains(page.Messages, m => m.Text.Contains("the 3rd"));

        var refused = await Assert.ThrowsAsync<BanterErrorException>(() => outside.RecallRoomAsync("#plans", "#main"));
        Assert.Equal("NOT_DISCLOSED", refused.Code);
    }

    [Fact]
    public async Task IsolatedNotEvenAnAgentInTheRoomMayCarryItOut()
    {
        await using var admin = await RoomWithASecretAsync("#plans", RoomDisclosure.Isolated);
        await using var inside = await AgentAsync("local", true, "#main", "#plans");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(() => inside.RecallRoomAsync("#plans", "#main"));
        Assert.Equal("NOT_DISCLOSED", refused.Code);

        // From inside, it is just the room.
        Assert.NotEmpty((await inside.RecallRoomAsync("#plans", "#plans")).Messages);
    }

    [Fact]
    public async Task ChildrenARoomBeneathMayDrawOnItAndOneElsewhereMayNot()
    {
        await using var admin = await RoomWithASecretAsync("#plans", RoomDisclosure.Children);
        await admin.JoinAsync("#plans");
        await admin.CreateRoomAsync("#plans-work", "#plans");
        await admin.CreateRoomAsync("#elsewhere");

        // Not in #plans at all - in a room beneath it.
        await using var worker = await AgentAsync("local", true, "#plans-work", "#elsewhere");
        Assert.NotEmpty((await worker.RecallRoomAsync("#plans", "#plans-work")).Messages);

        var refused = await Assert.ThrowsAsync<BanterErrorException>(() => worker.RecallRoomAsync("#plans", "#elsewhere"));
        Assert.Equal("NOT_DISCLOSED", refused.Code);

        // And asking "from" a room beneath it that you are not in does not count.
        await using var stranger = await AgentAsync("boss", true, "#elsewhere");
        var notIn = await Assert.ThrowsAsync<BanterErrorException>(() => stranger.RecallRoomAsync("#plans", "#plans-work"));
        Assert.Equal("NOT_IN_ROOM", notIn.Code);
    }

    [Fact]
    public async Task GlobalAnyAgentMayButClearanceStillApplies()
    {
        await using var admin = await RoomWithASecretAsync("#plans", RoomDisclosure.Global);
        await using var local = await AgentAsync("local", true, "#main");
        Assert.NotEmpty((await local.RecallRoomAsync("#plans", "#main")).Messages);

        // Global widens who may ask; it does not lower the bar. A third party is not cleared for a
        // room that may hold sensitive content - which is every room unless it says otherwise.
        await using var frontier = await AgentAsync("claude", false, "#main");
        var refused = await Assert.ThrowsAsync<BanterErrorException>(() => frontier.RecallRoomAsync("#plans", "#main"));
        Assert.Equal("NOT_CLEARED", refused.Code);
    }
}
