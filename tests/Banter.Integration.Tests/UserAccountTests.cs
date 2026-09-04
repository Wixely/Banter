using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Integration.Tests;

/// <summary>
/// Creating, resetting and removing user accounts while the server runs — the humans' half of the
/// admin story, over the real store with real hashing.
///
/// <para>The property these tests hold onto: <b>the server invents every password it hands out and
/// keeps none of them.</b> A temporary password exists in exactly one reply to exactly one admin;
/// after that the server holds a hash, and the only path forward is the owner changing it or an
/// admin resetting it. Nothing here can read a password back, and no test asserts otherwise.</para>
/// </summary>
public sealed class UserAccountTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();

    private string _root = null!;
    private BanterDatabase _database = null!;
    private DbAccountStore _accounts = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-users-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();

        // The real store, because half of what is under test IS the hashing: a temp password must
        // authenticate through PBKDF2 exactly as a human typing it would.
        _accounts = new DbAccountStore(_database);
        await _accounts.CreateUserAsync("root", "pw", isAdmin: true);
        await _accounts.CreateUserAsync("nell", "pw");

        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(
            _transport, _accounts, new DbServerStore(_database), files,
            identities: new AgentIdentityStore(_database),
            accountAdmin: _accounts);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private Task<BanterClient> ConnectAsync(string user, string pass) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, pass);

    [Fact]
    public async Task ACreatedUserSignsInWithTheTempPasswordTheAdminWasHanded()
    {
        await using var admin = await ConnectAsync("root", "pw");
        var created = await admin.CreateUserAsync("carol").WaitAsync(Patience);
        output.WriteLine($"temp password: {created.Password}");

        Assert.Equal("carol", created.Username);
        Assert.StartsWith("banter-temp-", created.Password, StringComparison.Ordinal);

        await using var carol = await ConnectAsync("carol", created.Password);
        Assert.Equal("carol", carol.Nick);
        Assert.False(carol.IsAdmin);

        var listed = await admin.ListUsersAsync().WaitAsync(Patience);
        Assert.Contains(listed, u => u.Username == "carol" && !u.IsAdmin);
    }

    [Fact]
    public async Task AnOrdinaryUserIsRefusedEveryAdminVerb()
    {
        await using var nell = await ConnectAsync("nell", "pw");

        // The block is gated as a block, so one probe per verb is the honest coverage.
        await AssertsRefusedAsync("NOT_ADMIN", () => nell.CreateUserAsync("mallory"));
        await AssertsRefusedAsync("NOT_ADMIN", () => nell.ListUsersAsync());
        await AssertsRefusedAsync("NOT_ADMIN", () => nell.DeleteUserAsync("root"));
        await AssertsRefusedAsync("NOT_ADMIN", () => nell.SetUserAdminAsync("nell", true));
        await AssertsRefusedAsync("NOT_ADMIN", () => nell.ResetUserPasswordAsync("root"));
    }

    [Fact]
    public async Task AResetKillsTheOldPasswordAtOnce()
    {
        await using var admin = await ConnectAsync("root", "pw");
        var reset = await admin.ResetUserPasswordAsync("nell").WaitAsync(Patience);

        // Old password: dead. New one: a working sign-in. The order matters — proving the new
        // password first would leave open whether the old one died.
        await Assert.ThrowsAsync<BanterAuthException>(() => ConnectAsync("nell", "pw").WaitAsync(Patience));
        await using var nell = await ConnectAsync("nell", reset.Password);
        Assert.Equal("nell", nell.Nick);
    }

    [Fact]
    public async Task ChangingYourOwnPasswordNeedsTheCurrentOne()
    {
        await using var nell = await ConnectAsync("nell", "pw");

        // A signed-in session is not proof enough: the machine may just be unlocked.
        await AssertsRefusedAsync("WRONG_PASSWORD", () => nell.ChangeMyPasswordAsync("guessed", "a-new-password"));
        await AssertsRefusedAsync("WEAK_PASSWORD", () => nell.ChangeMyPasswordAsync("pw", "short"));

        await nell.ChangeMyPasswordAsync("pw", "a-new-password").WaitAsync(Patience);

        await Assert.ThrowsAsync<BanterAuthException>(() => ConnectAsync("nell", "pw").WaitAsync(Patience));
        await using var again = await ConnectAsync("nell", "a-new-password");
        Assert.Equal("nell", again.Nick);
    }

    [Fact]
    public async Task TheLastAdminCannotDemoteThemselves()
    {
        await using var admin = await ConnectAsync("root", "pw");

        // Refused while alone: the change that leaves zero admins has no admin left to undo it.
        await AssertsRefusedAsync("LAST_ADMIN", () => admin.SetUserAdminAsync("root", false));

        // With a second admin standing, the same demotion is just a change like any other.
        await admin.SetUserAdminAsync("nell", true).WaitAsync(Patience);
        await admin.SetUserAdminAsync("root", false).WaitAsync(Patience);

        await using var nell = await ConnectAsync("nell", "pw");
        var users = await nell.ListUsersAsync().WaitAsync(Patience);
        Assert.Contains(users, u => u.Username == "root" && !u.IsAdmin);
    }

    [Fact]
    public async Task AnAdminCannotRemoveThemselvesButCanRemoveAnother()
    {
        await using var admin = await ConnectAsync("root", "pw");

        await AssertsRefusedAsync("NOT_YOURSELF", () => admin.DeleteUserAsync("root"));

        await admin.DeleteUserAsync("nell").WaitAsync(Patience);
        await Assert.ThrowsAsync<BanterAuthException>(() => ConnectAsync("nell", "pw").WaitAsync(Patience));
    }

    [Fact]
    public async Task ANickBelongsToOneThingOnlyAcrossBothPages()
    {
        await using var admin = await ConnectAsync("root", "pw");
        await admin.CreateAgentAsync("scribe", ["#main"], ["chat"]).WaitAsync(Patience);

        // A user cannot shadow an agent identity: two answers to one name in a room is the
        // confusion both admin pages exist to prevent.
        await AssertsRefusedAsync("NICK_TAKEN", () => admin.CreateUserAsync("scribe"));
        await AssertsRefusedAsync("NICK_TAKEN", () => admin.CreateUserAsync("root"));
    }

    [Fact]
    public async Task TheJunkNickIsRefusedBeforeItBecomesAnAccount()
    {
        await using var admin = await ConnectAsync("root", "pw");
        await AssertsRefusedAsync("BAD_NICK", () => admin.CreateUserAsync("has spaces"));
        await AssertsRefusedAsync("BAD_NICK", () => admin.CreateUserAsync("#room"));
        await AssertsRefusedAsync("BAD_NICK", () => admin.CreateUserAsync("x"));
    }

    private static async Task AssertsRefusedAsync(string code, Func<Task> act)
    {
        var refusal = await Assert.ThrowsAsync<BanterErrorException>(act);
        Assert.Equal(code, refusal.Code);
    }
}
