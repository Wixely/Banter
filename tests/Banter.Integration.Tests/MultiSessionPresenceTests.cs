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

    private static readonly PresenceLimits Grace = new()
    {
        ReconnectGrace = TimeSpan.FromMilliseconds(400),
        SweepInterval = TimeSpan.FromMilliseconds(50),
    };
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

        // A real grace, shortened. The production default is ten seconds, which is the point of
        // it - a phone out of the foreground is back well inside that - and a test that waited
        // ten seconds per departure would be a test nobody runs. Not ZERO, because that turns the
        // behaviour off and these are the tests that most need it on.
        _server = new BanterServer(
            _transport, _accounts, new DbServerStore(_database), files,
            presence: Grace);
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

        // After the grace, not instead of it: nobody came back, so this is somebody who left.
        Assert.Equal("alice", await parted.Task.WaitAsync(Timeout));
    }

    /// <summary>
    /// A pocket trip says nothing at all. Android destroys the socket when the app leaves the
    /// foreground, so this used to be a PART and then a JOIN in every room the account was in -
    /// for somebody who never went anywhere, and loudest for an admin who is in all of them.
    ///
    /// <para>Both halves are asserted, because suppressing only the PART would announce the return
    /// of somebody the room was never told had gone: the same noise with half the words.</para>
    /// </summary>
    [Fact]
    public async Task APocketTripAnnouncesNothing()
    {
        await using var bob = await ConnectAsync("bob");
        await bob.JoinAsync("#main");

        var phone = await ConnectAsync("alice");
        await phone.JoinAsync("#main");

        var said = new List<string>();
        bob.MemberParted += p => said.Add($"part {p.Nick}");
        bob.MemberJoined += j => said.Add($"join {j.Nick}");

        // Out of the foreground and back, well inside the grace.
        await phone.DisposeAsync();
        await Task.Delay(100);

        await using var returned = await ConnectAsync("alice");
        await returned.JoinAsync("#main");

        // Long enough that a sweep has certainly run and found nothing to announce.
        await Task.Delay(400);

        Assert.Empty(said);

        // And alice is in the room throughout, which is the state the silence is claiming.
        var members = await bob.GetMembersAsync("#main");
        Assert.Contains(members.Members, m => m.Nick == "alice");
    }

    /// <summary>
    /// The grace is not a licence to go quiet for ever: a session that does not come back is
    /// announced once the window passes, and the room is not left holding a member who is gone.
    /// </summary>
    [Fact]
    public async Task APhoneThatDoesNotComeBackIsAnnouncedOnce()
    {
        await using var bob = await ConnectAsync("bob");
        await bob.JoinAsync("#main");

        var phone = await ConnectAsync("alice");
        await phone.JoinAsync("#main");

        var parts = new List<string>();
        bob.MemberParted += p => parts.Add(p.Nick ?? "");

        await phone.DisposeAsync();
        await Task.Delay(1000);          // the grace is 400ms and the sweep runs every 50ms

        Assert.Equal(["alice"], parts);

        var members = await bob.GetMembersAsync("#main");
        Assert.DoesNotContain(members.Members, m => m.Nick == "alice");
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
