using Banter.Core;
using Banter.Server.Persistence;
using Xunit;

namespace Banter.Server.Tests;

/// <summary>
/// Which hub user each agent is.
///
/// <para>This is the record that makes the hub the authority rather than an advisor: a call for an
/// agent is made as that agent, so the hub refuses what it did not grant instead of trusting this
/// server to have filtered first — and an agent that went around Banter with the same key would be
/// told exactly the same thing.</para>
/// </summary>
public sealed class HubIdentityStoreTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private BanterDatabase _database = null!;
    private HubIdentityStore _store = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-hubid-{Guid.NewGuid():N}.db");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
        _store = new HubIdentityStore(_database);
    }

    public Task DisposeAsync()
    {
        BanterDatabase.ClearSqlitePools();
        File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    private static HubIdentity Issued(string userId = "u1", string token = "mcphub_abc") =>
        new(userId, token, DateTimeOffset.UtcNow);

    /// <summary>An agent nobody has issued a key to has none: it is not assumed to be anybody.</summary>
    [Fact]
    public async Task An_agent_with_no_identity_has_none()
    {
        Assert.Null(await _store.ForAgentAsync("hub", "dagger"));
        Assert.Empty(await _store.AgentsAsync("hub"));
    }

    [Fact]
    public async Task An_issued_identity_comes_back_with_its_key()
    {
        var identity = Issued("user-1", "mcphub_dagger_key");

        await _store.SaveAsync("hub", "dagger", identity);

        var stored = await _store.ForAgentAsync("hub", "dagger");
        Assert.NotNull(stored);
        Assert.Equal("user-1", stored.UserId);
        Assert.Equal("mcphub_dagger_key", stored.Token);
        Assert.Equal(identity.IssuedAtUtc.ToUnixTimeSeconds(), stored.IssuedAtUtc.ToUnixTimeSeconds());
    }

    /// <summary>
    /// The hub retires the old key the moment it issues a new one, so keeping the previous row
    /// would only preserve a key that no longer works.
    /// </summary>
    [Fact]
    public async Task Rotating_replaces_the_key_rather_than_keeping_both()
    {
        await _store.SaveAsync("hub", "dagger", Issued("user-1", "first"));
        await _store.SaveAsync("hub", "dagger", Issued("user-1", "second"));

        var stored = await _store.ForAgentAsync("hub", "dagger");
        Assert.Equal("second", stored!.Token);
        Assert.Single(await _store.AgentsAsync("hub"));
    }

    /// <summary>
    /// Two hubs issue two different keys to the same agent. One row keyed by agent alone could
    /// only ever be right about one of them, and would send the wrong key to the other.
    /// </summary>
    [Fact]
    public async Task One_agent_can_be_a_different_user_on_each_hub()
    {
        await _store.SaveAsync("hub", "dagger", Issued("user-1", "key-for-hub"));
        await _store.SaveAsync("other", "dagger", Issued("user-9", "key-for-other"));

        Assert.Equal("key-for-hub", (await _store.ForAgentAsync("hub", "dagger"))!.Token);
        Assert.Equal("key-for-other", (await _store.ForAgentAsync("other", "dagger"))!.Token);
    }

    [Fact]
    public async Task Agents_are_listed_without_their_keys()
    {
        await _store.SaveAsync("hub", "dagger", Issued("user-1", "secret-key"));
        await _store.SaveAsync("hub", "scout", Issued("user-2", "another-secret"));

        var agents = await _store.AgentsAsync("hub");

        Assert.Equal(["dagger", "scout"], agents.Keys.Order());
        Assert.Equal("user-1", agents["dagger"]);
        Assert.DoesNotContain("secret-key", string.Join(" ", agents.Values), StringComparison.Ordinal);
    }

    /// <summary>
    /// Forgetting is Banter's own bookkeeping: the user still exists on the hub, which is the only
    /// thing that can retire a key. Removing one agent leaves the others alone.
    /// </summary>
    [Fact]
    public async Task Forgetting_one_agent_leaves_the_rest()
    {
        await _store.SaveAsync("hub", "dagger", Issued("user-1"));
        await _store.SaveAsync("hub", "scout", Issued("user-2"));

        await _store.RemoveAsync("hub", "dagger");

        Assert.Null(await _store.ForAgentAsync("hub", "dagger"));
        Assert.NotNull(await _store.ForAgentAsync("hub", "scout"));
    }

    [Fact]
    public async Task Forgetting_an_agent_that_was_never_issued_one_is_not_an_error()
    {
        await _store.RemoveAsync("hub", "never-existed");

        Assert.Empty(await _store.AgentsAsync("hub"));
    }
}
