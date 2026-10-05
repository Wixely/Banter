using System.Text.Json;
using Banter.Core;
using Banter.Server.Persistence;
using Banter.Server.Tools;
using Xunit;

namespace Banter.Server.Tests;

/// <summary>
/// Giving an agent an identity on the hub, and telling the hub what it may use.
///
/// <para>Banter manages; the hub decides. What matters here is that this server never becomes the
/// second answer to "what may this agent use" — it asks the hub, and the only thing it stores is
/// the one thing the hub cannot tell it twice, which is the key shown once.</para>
/// </summary>
public sealed class HubProvisionerTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private BanterDatabase _database = null!;
    private HubIdentityStore _identities = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-prov-{Guid.NewGuid():N}.db");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
        _identities = new HubIdentityStore(_database);
    }

    public Task DisposeAsync()
    {
        BanterDatabase.ClearSqlitePools();
        File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    private HubProvisioner Provisioner(IHubAdmin hub) => new(hub, _identities, "hub");

    /// <summary>A hub that answers the way MCPHub does, and remembers what it was asked.</summary>
    private sealed class FakeHub : IHubAdmin
    {
        private int _issued;

        public List<string> Calls { get; } = [];

        public Dictionary<string, string[]> Grants { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Deleted { get; } = new(StringComparer.Ordinal);

        public HubAdminException? Refuse { get; set; }

        public Task<JsonElement> CallAsync(
            string tool,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (Refuse is { } refusal)
            {
                throw refusal;
            }

            var json = tool switch
            {
                "users__create" => Issue($"user-{++_issued}"),
                "users__rotate_key" => Issue((string)arguments!["user"]!),
                "users__delete" => Delete((string)arguments!["user"]!),
                "permissions__set_grants" => SetGrants(arguments!),
                "permissions__list_grants" => ListGrants(),
                _ => throw new HubAdminException("hub.unavailable", $"no such tool '{tool}'"),
            };

            return Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
        }

        private string Issue(string userId) =>
            $$"""{"user":{"id":"{{userId}}","name":"agent","enabled":true},"key":"mcphub_key_{{userId}}_{{++_issued}}"}""";

        private string Delete(string userId)
        {
            Deleted.Add(userId);
            Grants.Remove(userId);
            return """{"message":"gone"}""";
        }

        private string SetGrants(IReadOnlyDictionary<string, object?> arguments)
        {
            Grants[(string)arguments["user"]!] = [.. (IReadOnlyList<string>)arguments["tools"]!];
            return """{"message":"saved"}""";
        }

        private string ListGrants()
        {
            var entries = Grants.Select(g =>
                $$"""{"userId":"{{g.Key}}","userName":"agent","tools":[{{string.Join(",", g.Value.Select(t => $"\"{t}\""))}}]}""");
            return $$"""{"grants":[{{string.Join(",", entries)}}]}""";
        }
    }

    private sealed class ShapelessHub : IHubAdmin
    {
        public Task<JsonElement> CallAsync(
            string tool,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonDocument.Parse("""{"user":{"id":"u1"}}""").RootElement.Clone());
    }

    // ---- minting -------------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_with_no_identity_is_given_one_and_its_key_is_kept()
    {
        var hub = new FakeHub();

        var identity = await Provisioner(hub).EnsureAsync("dagger");

        Assert.Equal("user-1", identity.UserId);
        Assert.StartsWith("mcphub_key_", identity.Token, StringComparison.Ordinal);
        Assert.Equal("users__create", Assert.Single(hub.Calls));

        // Kept, because a hub shows a key once: asking again must not mean asking the hub again.
        Assert.Equal(identity.Token, (await _identities.ForAgentAsync("hub", "dagger"))!.Token);
    }

    /// <summary>
    /// A second users__create would make a second user rather than return the first, so an agent
    /// we already hold an identity for is never re-created.
    /// </summary>
    [Fact]
    public async Task An_agent_that_already_has_one_is_not_given_another()
    {
        var hub = new FakeHub();
        var provisioner = Provisioner(hub);

        var first = await provisioner.EnsureAsync("dagger");
        var second = await provisioner.EnsureAsync("dagger");

        Assert.Equal(first.Token, second.Token);
        Assert.Single(hub.Calls);
    }

    [Fact]
    public async Task Rotating_issues_a_new_key_for_the_same_user()
    {
        var hub = new FakeHub();
        var provisioner = Provisioner(hub);
        var before = await provisioner.EnsureAsync("dagger");

        var after = await provisioner.RotateAsync("dagger");

        Assert.Equal(before.UserId, after.UserId);
        Assert.NotEqual(before.Token, after.Token);
        Assert.Equal(after.Token, (await _identities.ForAgentAsync("hub", "dagger"))!.Token);
        Assert.Contains("users__rotate_key", hub.Calls);
    }

    // ---- grants --------------------------------------------------------------------------------

    /// <summary>
    /// Granting goes to the hub and comes back from the hub. Nothing here keeps a copy: a local one
    /// would be a second answer, and would drift the moment somebody changed it in the hub's own UI.
    /// </summary>
    [Fact]
    public async Task Grants_are_written_to_the_hub_and_read_back_from_it()
    {
        var hub = new FakeHub();
        var provisioner = Provisioner(hub);

        await provisioner.SetGrantsAsync("dagger", ["kodi__*", "redis__get"]);

        Assert.Equal(["kodi__*", "redis__get"], hub.Grants["user-1"]);
        Assert.Equal(["kodi__*", "redis__get"], await provisioner.GrantsAsync("dagger"));
    }

    [Fact]
    public async Task Granting_an_agent_that_has_no_identity_gives_it_one_first()
    {
        var hub = new FakeHub();

        await Provisioner(hub).SetGrantsAsync("newcomer", ["kodi__*"]);

        Assert.Contains("users__create", hub.Calls);
        Assert.NotNull(await _identities.ForAgentAsync("hub", "newcomer"));
    }

    [Fact]
    public async Task An_agent_the_hub_has_never_heard_of_is_granted_nothing()
    {
        Assert.Empty(await Provisioner(new FakeHub()).GrantsAsync("stranger"));
    }

    // ---- removal -------------------------------------------------------------------------------

    /// <summary>
    /// Deleted there as well as forgotten here: a user left behind holds a key this server no
    /// longer tracks, which is the one state nobody can reason about.
    /// </summary>
    [Fact]
    public async Task Removing_deletes_the_user_on_the_hub_and_forgets_it_here()
    {
        var hub = new FakeHub();
        var provisioner = Provisioner(hub);
        var identity = await provisioner.EnsureAsync("dagger");

        await provisioner.RemoveAsync("dagger");

        Assert.Contains(identity.UserId, hub.Deleted);
        Assert.Null(await _identities.ForAgentAsync("hub", "dagger"));
    }

    [Fact]
    public async Task Removing_an_agent_already_gone_from_the_hub_still_forgets_it_here()
    {
        var hub = new FakeHub();
        var provisioner = Provisioner(hub);
        await provisioner.EnsureAsync("dagger");
        hub.Refuse = new HubAdminException("users.no_such_user", "No user has that id.");

        await provisioner.RemoveAsync("dagger");

        Assert.Null(await _identities.ForAgentAsync("hub", "dagger"));
    }

    // ---- a hub that will not play --------------------------------------------------------------

    /// <summary>
    /// With administration off the management tools are absent rather than refused, so the message
    /// has to name the switch — otherwise an operator is left looking at a hub that appears fine.
    /// </summary>
    [Fact]
    public async Task A_hub_that_offers_no_management_tools_says_which_switch_to_turn_on()
    {
        var hub = new FakeHub
        {
            Refuse = new HubAdminException(
                "hub.unavailable",
                "This hub does not offer 'hub__users__create'.",
                "Turn on MCPHUB_ADMINISTRATION_ENABLED."),
        };

        var thrown = await Assert.ThrowsAsync<HubAdminException>(() => Provisioner(hub).EnsureAsync("dagger"));

        Assert.Equal("hub.unavailable", thrown.Code);
        Assert.Contains("MCPHUB_ADMINISTRATION_ENABLED", thrown.Message, StringComparison.Ordinal);
        Assert.Null(await _identities.ForAgentAsync("hub", "dagger"));
    }

    /// <summary>
    /// A key arrives once. If the reply is not the shape we expect that key is already lost, so
    /// storing half an identity would leave an agent that can never authenticate and a user on the
    /// hub nobody can use.
    /// </summary>
    [Fact]
    public async Task A_reply_with_no_key_in_it_stores_nothing()
    {
        await Assert.ThrowsAsync<HubAdminException>(() => Provisioner(new ShapelessHub()).EnsureAsync("dagger"));

        Assert.Null(await _identities.ForAgentAsync("hub", "dagger"));
    }
}
