using Banter.Core;
using Banter.Server.Persistence;
using Dapper;
using Xunit;

namespace Banter.Server.Tests;

public sealed class PersistenceTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private BanterDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-test-{Guid.NewGuid():N}.db");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        BanterDatabase.ClearSqlitePools();
        File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ManifestAppliesEachMigrationExactlyOnce()
    {
        // A second initialize must be a no-op, not a re-run.
        await _database.InitializeAsync();

        await using var connection = await _database.OpenAsync();
        var manifest = (await connection.QueryAsync<int>("SELECT id FROM schema_manifest ORDER BY id")).ToArray();
        Assert.Equal(SchemaManifest.Migrations.Select(m => m.Id).ToArray(), manifest);
    }

    [Fact]
    public void MigrationIdsAreSequentialAndDialectsPaired()
    {
        var expected = 1;
        foreach (var migration in SchemaManifest.Migrations)
        {
            Assert.Equal(expected++, migration.Id);
            Assert.False(string.IsNullOrWhiteSpace(migration.SqliteSql));
            Assert.False(string.IsNullOrWhiteSpace(migration.PostgresSql));
        }
    }

    [Fact]
    public async Task AccountsRoundTripWithHashedCredentials()
    {
        var accounts = new DbAccountStore(_database);
        await accounts.CreateUserAsync("Alice", "s3cret", isAgent: false);

        var ok = await accounts.AuthenticateAsync("alice", "s3cret");
        Assert.NotNull(ok);
        Assert.Equal("alice", ok.Username);
        Assert.False(ok.IsAgent);

        // Case-insensitive lookup, wrong secret rejected.
        Assert.NotNull(await accounts.AuthenticateAsync("ALICE", "s3cret"));
        Assert.Null(await accounts.AuthenticateAsync("alice", "wrong"));
        Assert.Null(await accounts.AuthenticateAsync("nobody", "s3cret"));

        // The secret itself must not be in the database.
        await using var connection = await _database.OpenAsync();
        var stored = await connection.QuerySingleAsync<byte[]>("SELECT password_hash FROM accounts WHERE username = 'alice'");
        Assert.NotEqual("s3cret"u8.ToArray(), stored);
    }

    [Fact]
    public async Task AgentFlagPersists()
    {
        var accounts = new DbAccountStore(_database);
        await accounts.CreateUserAsync("dagger", "pw", isAgent: true);
        var agent = await accounts.AuthenticateAsync("dagger", "pw");
        Assert.NotNull(agent);
        Assert.True(agent.IsAgent);
    }

    [Fact]
    public async Task HistoryPagesOldestFirstWithCursor()
    {
        var store = new DbServerStore(_database);
        for (var i = 1; i <= 3; i++)
        {
            await store.AppendMessageAsync(new ChatMessage($"id-{i}", "#room", "alice", $"message {i}", i * 1000, null));
        }

        var latest = await store.GetHistoryPageAsync("#room", beforeMessageId: null, limit: 2);
        Assert.NotNull(latest);
        Assert.Equal(["message 2", "message 3"], latest.Messages.Select(m => m.Text).ToArray());
        Assert.Equal("id-2", latest.NextCursor);

        var older = await store.GetHistoryPageAsync("#room", beforeMessageId: latest.NextCursor, limit: 2);
        Assert.NotNull(older);
        Assert.Equal(["message 1"], older.Messages.Select(m => m.Text).ToArray());
        Assert.Null(older.NextCursor);

        Assert.Null(await store.GetHistoryPageAsync("#room", beforeMessageId: "no-such-id", limit: 2));
    }

    [Fact]
    public async Task HistoryIsScopedPerRoom()
    {
        var store = new DbServerStore(_database);
        await store.AppendMessageAsync(new ChatMessage("a", "#one", "alice", "in one", 1, null));
        await store.AppendMessageAsync(new ChatMessage("b", "#two", "alice", "in two", 2, null));

        var page = await store.GetHistoryPageAsync("#one", null, 10);
        Assert.NotNull(page);
        var only = Assert.Single(page.Messages);
        Assert.Equal("in one", only.Text);
    }

    [Fact]
    public async Task RoomUpsertInsertsThenUpdatesTopic()
    {
        var store = new DbServerStore(_database);
        await store.UpsertRoomAsync("#main", null);
        await store.UpsertRoomAsync("#main", "welcome");

        var room = Assert.Single(await store.GetRoomsAsync());
        Assert.Equal("#main", room.Name);
        Assert.Equal("welcome", room.Topic);
    }

    [Fact]
    public void PostgresOptionsParseAndSqliteIsDefault()
    {
        Assert.Equal(StorageProvider.Sqlite, BanterStorageOptions.Parse(null, null).Provider);
        var pg = BanterStorageOptions.Parse("postgres", "Host=db;Database=banter;Username=u;Password=p");
        Assert.Equal(StorageProvider.Postgres, pg.Provider);
        Assert.Throws<ArgumentException>(() => BanterStorageOptions.Parse("postgres", null));
        Assert.Throws<ArgumentException>(() => BanterStorageOptions.Parse("mysql", "x"));
    }
}
