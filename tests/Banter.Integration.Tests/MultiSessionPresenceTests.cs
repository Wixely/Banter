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
/// One user, several live clients — desktop and phone at once.
///
/// <para>Banter deliberately does not use IRC's <c>alice</c> / <c>alice^mobile</c> convention:
/// identity is the account, and a session is just one of its connections. These cover the
/// presence semantics that follow from that, which are the parts easy to get wrong.</para>
/// </summary>
public sealed class MultiSessionPresenceTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("alice", "pw")
        .AddUser("bob", "pw");

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-multisession-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task AUserOnTwoDevicesAppearsOnceInTheMemberList()
    {
        await using var bob = await ConnectAsync("bob");
        await bob.JoinAsync("#main");

        await using var desktop = await ConnectAsync("alice");
        await using var phone = await ConnectAsync("alice");
        await desktop.JoinAsync("#main");
        await phone.JoinAsync("#main");

        var members = await bob.GetMembersAsync("#main");

        // One person is one entry, however many things they are logged in on.
        Assert.Equal(1, members.Members.Count(m => m.Nick == "alice"));
    }

    [Fact]
    public async Task BothDevicesReceiveRoomMessages()
    {
        await using var bob = await ConnectAsync("bob");
        await bob.JoinAsync("#main");

        await using var desktop = await ConnectAsync("alice");
        await using var phone = await ConnectAsync("alice");
        await desktop.JoinAsync("#main");
        await phone.JoinAsync("#main");

        var onDesktop = new TaskCompletionSource<string>();
        var onPhone = new TaskCompletionSource<string>();
        desktop.MessageReceived += m => { if (m.Sender == "bob") onDesktop.TrySetResult(m.Text); };
        phone.MessageReceived += m => { if (m.Sender == "bob") onPhone.TrySetResult(m.Text); };

        await bob.SendMessageAsync("#main", "hello alice");

        Assert.Equal("hello alice", await onDesktop.Task.WaitAsync(Timeout));
        Assert.Equal("hello alice", await onPhone.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task ClosingOneDeviceDoesNotAnnounceThatTheUserLeft()
    {
        await using var bob = await ConnectAsync("bob");
        await bob.JoinAsync("#main");

        var desktop = await ConnectAsync("alice");
        await using var phone = await ConnectAsync("alice");
        await desktop.JoinAsync("#main");
        await phone.JoinAsync("#main");

        var parted = new List<string>();
        bob.MemberParted += p => parted.Add(p.Nick ?? "");

        // Shutting the laptop is not leaving the room.
        await desktop.DisposeAsync();
        await Task.Delay(500);

        Assert.DoesNotContain("alice", parted);

        var members = await bob.GetMembersAsync("#main");
        Assert.Contains(members.Members, m => m.Nick == "alice");
    }

    [Fact]
    public async Task TheLastDeviceLeavingDoesAnnounceIt()
    {
        await using var bob = await ConnectAsync("bob");
        await bob.JoinAsync("#main");

        var desktop = await ConnectAsync("alice");
        var phone = await ConnectAsync("alice");
        await desktop.JoinAsync("#main");
        await phone.JoinAsync("#main");

        var parted = new TaskCompletionSource<string>();
        bob.MemberParted += p => { if (p.Nick == "alice") parted.TrySetResult(p.Nick!); };

        await desktop.DisposeAsync();
        await Task.Delay(300);
        await phone.DisposeAsync();

        Assert.Equal("alice", await parted.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task ASecondDeviceJoiningDoesNotAnnounceASecondArrival()
    {
        await using var bob = await ConnectAsync("bob");
        await bob.JoinAsync("#main");

        var joined = new List<string>();
        bob.MemberJoined += j => joined.Add(j.Nick ?? "");

        await using var desktop = await ConnectAsync("alice");
        await desktop.JoinAsync("#main");
        await Task.Delay(300);
        await using var phone = await ConnectAsync("alice");
        await phone.JoinAsync("#main");
        await Task.Delay(300);

        // "alice joined" twice would read as two people arriving.
        Assert.Equal(1, joined.Count(n => n == "alice"));
    }

    [Fact]
    public async Task PartingFromOneDeviceLeavesTheRoomForAllOfThem()
    {
        await using var bob = await ConnectAsync("bob");
        await bob.JoinAsync("#main");

        await using var desktop = await ConnectAsync("alice");
        await using var phone = await ConnectAsync("alice");
        await desktop.JoinAsync("#main");
        await phone.JoinAsync("#main");

        // An explicit /part is the person leaving, not the device - otherwise leaving on your
        // laptop and still getting messages on your phone would be baffling.
        await desktop.PartAsync("#main");
        await Task.Delay(400);

        var members = await bob.GetMembersAsync("#main");
        Assert.DoesNotContain(members.Members, m => m.Nick == "alice");
    }
}
