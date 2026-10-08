using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>
/// The other half of the departure grace: what happens when nobody comes back.
///
/// <para>Separated from <see cref="MultiSessionPresenceTests"/> because the two need opposite
/// things of the clock. Those tests assert that nothing was announced, so they want a window long
/// enough that no amount of load can close it. These assert that something WAS announced once the
/// window shut, so they have to wait it out - and a window short enough to wait for is one a slow
/// reconnect can fall outside.</para>
///
/// <para>Sharing one server made the pocket-trip test fail under the whole suite's load while
/// passing on its own, which is the most expensive kind of test failure: it looks like the product
/// and it is the fixture.</para>
/// </summary>
public sealed class DepartureGraceTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Short, because these tests wait for it to pass. The production default is ten
    /// seconds - see PresenceLimits, which explains why it is that and not this.</summary>
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
        _root = Path.Combine(Path.GetTempPath(), $"banter-departure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();

        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(
            _transport, _accounts, new DbServerStore(_database), files, presence: Grace);
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
        await Task.Delay(1000);          // past the 400ms grace, with the sweep running every 50ms

        Assert.Equal(["alice"], parts);

        var members = await bob.GetMembersAsync("#main");
        Assert.DoesNotContain(members.Members, m => m.Nick == "alice");
    }
}
