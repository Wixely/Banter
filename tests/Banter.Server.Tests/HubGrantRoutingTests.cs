using System.Text.Json;
using Banter.Core;
using Banter.Server.Persistence;
using Banter.Server.Tools;
using Xunit;

namespace Banter.Server.Tests;

/// <summary>
/// Where a grant is written, and where it is read back from.
///
/// <para>Banter grants the upstreams that have no idea who is calling. The ones that decide for
/// themselves are told instead, and asked. Both come back under the names this server advertises,
/// so the tool panel shows one list and nobody has to know the difference — but writing a
/// self-enforcing upstream's grant into this server's store would be a tick on that panel that
/// changes nothing at all, which is the failure these pin.</para>
/// </summary>
public sealed class HubGrantRoutingTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private BanterDatabase _database = null!;
    private ToolGrantStore _grants = null!;
    private HubIdentityStore _identities = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-grants-{Guid.NewGuid():N}.db");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
        _grants = new ToolGrantStore(_database);
        _identities = new HubIdentityStore(_database);
    }

    public Task DisposeAsync()
    {
        BanterDatabase.ClearSqlitePools();
        File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    /// <summary>A hub that remembers what it was granted, answering as MCPHub does.</summary>
    private sealed class FakeHub : IHubAdmin
    {
        private int _issued;

        public Dictionary<string, string[]> Grants { get; } = new(StringComparer.Ordinal);

        public Task<JsonElement> CallAsync(
            string tool,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken = default)
        {
            var json = tool switch
            {
                "users__create" => Issue(),
                "permissions__set_grants" => Set(arguments!),
                "permissions__list_grants" => List(),
                _ => throw new HubAdminException("hub.unavailable", $"no such tool '{tool}'"),
            };

            return Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
        }

        private string Issue()
        {
            var id = $"user-{++_issued}";
            return $$"""{"user":{"id":"{{id}}","name":"agent"},"key":"mcphub_{{id}}"}""";
        }

        private string Set(IReadOnlyDictionary<string, object?> arguments)
        {
            Grants[(string)arguments["user"]!] = [.. (IReadOnlyList<string>)arguments["tools"]!];
            return """{"message":"saved"}""";
        }

        private string List()
        {
            var entries = Grants.Select(g =>
                $$"""{"userId":"{{g.Key}}","tools":[{{string.Join(",", g.Value.Select(t => $"\"{t}\""))}}]}""");
            return $$"""{"grants":[{{string.Join(",", entries)}}]}""";
        }
    }

    private (McpToolBroker Broker, FakeHub Hub) Build()
    {
        var hub = new FakeHub();
        var options = new McpOptions
        {
            Upstreams =
            [
                new McpUpstreamConfig
                {
                    Key = "hub", Url = "http://127.0.0.1:1/mcp", PerAgentIdentity = true,
                },
                new McpUpstreamConfig { Key = "local", Command = "noop" },
            ],
        };

        return (new McpToolBroker(options, _grants, _identities, loggerFactory: null, hubAdmin: _ => hub), hub);
    }

    /// <summary>
    /// The split. A hub tool goes to the hub, under the name the hub knows it by — the two differ
    /// by exactly the upstream's prefix, and granting `hub__recipes__list` there would be granting
    /// a tool nobody has.
    /// </summary>
    [Fact]
    public async Task A_hub_tool_is_granted_on_the_hub_and_a_local_one_here()
    {
        var (broker, hub) = Build();

        await broker.SetGrantsAsync("dagger", ["hub__recipes__list", "local__do_thing"]);

        Assert.Equal(["recipes__list"], hub.Grants["user-1"]);
        Assert.Equal(["local__do_thing"], await _grants.ForAgentAsync("dagger"));
    }

    /// <summary>And back out again under this server's names, as one list.</summary>
    [Fact]
    public async Task Grants_come_back_as_one_list_under_the_names_this_server_advertises()
    {
        var (broker, _) = Build();
        await broker.SetGrantsAsync("dagger", ["hub__recipes__list", "local__do_thing"]);

        var held = await broker.GrantsForAsync("dagger");

        Assert.Equal(["hub__recipes__list", "local__do_thing"], held.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Revoking has to reach the hub too. Writing an empty list only here would leave the hub still
    /// granting what somebody believed they had taken away.
    /// </summary>
    [Fact]
    public async Task Revoking_everything_reaches_the_hub_as_well()
    {
        var (broker, hub) = Build();
        await broker.SetGrantsAsync("dagger", ["hub__recipes__list", "local__do_thing"]);

        await broker.SetGrantsAsync("dagger", []);

        Assert.Empty(hub.Grants["user-1"]);
        Assert.Empty(await _grants.ForAgentAsync("dagger"));
        Assert.Empty(await broker.GrantsForAsync("dagger"));
    }

    /// <summary>
    /// Granting on the hub mints the agent's identity there, since a grant for an agent that cannot
    /// authenticate is a grant nobody holds.
    /// </summary>
    [Fact]
    public async Task Granting_a_hub_tool_gives_the_agent_an_identity_there()
    {
        var (broker, _) = Build();

        await broker.SetGrantsAsync("dagger", ["hub__recipes__list"]);

        Assert.NotNull(await _identities.ForAgentAsync("hub", "dagger"));
    }

    /// <summary>
    /// An agent granted nothing on the hub still has its local grants, and the hub's silence is not
    /// mistaken for this server's.
    /// </summary>
    [Fact]
    public async Task Local_grants_stand_on_their_own()
    {
        var (broker, hub) = Build();

        await broker.SetGrantsAsync("scout", ["local__do_thing"]);

        Assert.Equal(["local__do_thing"], await broker.GrantsForAsync("scout"));
        Assert.Empty(hub.Grants.Values.SelectMany(g => g));
    }

    /// <summary>
    /// Without somewhere to remember who an agent is on the hub, that upstream is not treated as
    /// self-enforcing at all — so its tools are granted here rather than written into a hub this
    /// server cannot authenticate to.
    /// </summary>
    [Fact]
    public async Task With_no_identity_store_nothing_is_sent_to_the_hub()
    {
        var hub = new FakeHub();
        var options = new McpOptions
        {
            Upstreams = [new McpUpstreamConfig { Key = "hub", Url = "http://127.0.0.1:1/mcp", PerAgentIdentity = true }],
        };
        var broker = new McpToolBroker(options, _grants, identities: null, loggerFactory: null, hubAdmin: _ => hub);

        await broker.SetGrantsAsync("dagger", ["hub__recipes__list"]);

        Assert.Empty(hub.Grants);
        Assert.Equal(["hub__recipes__list"], await _grants.ForAgentAsync("dagger"));
    }
}
