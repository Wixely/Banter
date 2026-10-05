using System.Text.Json;
using Banter.Core;
using Banter.Protocol;
using Banter.Server.Persistence;
using Banter.Server.Tools;
using Xunit;

namespace Banter.Server.Tests;

/// <summary>
/// What an operator can see of the hubs this server defers to.
///
/// <para>The question this exists to answer is "why can this agent not use that tool", which since
/// the hub became the authority is no longer answerable from anything here alone. So: is the hub
/// reachable, will it let us manage it, who has an identity on it, and what does it grant them.</para>
/// </summary>
public sealed class HubInspectionTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private BanterDatabase _database = null!;
    private ToolGrantStore _grants = null!;
    private HubIdentityStore _identities = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-inspect-{Guid.NewGuid():N}.db");
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

    private sealed class FakeHub : IHubAdmin
    {
        private int _issued;

        public Dictionary<string, string[]> Grants { get; } = new(StringComparer.Ordinal);

        public bool Refusing { get; set; }

        public Task<JsonElement> CallAsync(
            string tool,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken = default)
        {
            if (Refusing)
            {
                throw new HubAdminException("hub.unavailable", "This hub offers no management tools.");
            }

            var json = tool switch
            {
                "users__create" => $$"""{"user":{"id":"user-{{++_issued}}"},"key":"mcphub_k{{_issued}}"}""",
                "permissions__set_grants" => Set(arguments!),
                "permissions__list_grants" => List(),
                _ => throw new HubAdminException("hub.unavailable", $"no such tool '{tool}'"),
            };

            return Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
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
                    Key = "hub", DisplayName = "MCPHub", Url = "http://127.0.0.1:1/mcp", PerAgentIdentity = true,
                },
            ],
        };

        return (new McpToolBroker(options, _grants, _identities, loggerFactory: null, hubAdmin: _ => hub), hub);
    }

    [Fact]
    public async Task A_server_with_no_hubs_reports_none()
    {
        var broker = new McpToolBroker(new McpOptions(), _grants, _identities);

        Assert.Empty(await broker.InspectHubsAsync());
    }

    /// <summary>
    /// A hub nothing has connected to is still worth listing: its state is the answer to why its
    /// tools are missing, and leaving it out would look like it was never configured.
    /// </summary>
    [Fact]
    public async Task An_unreachable_hub_is_listed_with_its_state()
    {
        var (broker, _) = Build();

        var hub = Assert.Single(await broker.InspectHubsAsync());

        Assert.Equal("hub", hub.Key);
        Assert.Equal("MCPHub", hub.DisplayName);
        Assert.NotEqual("Connected", hub.State);
        Assert.Empty(hub.Agents);
    }

    /// <summary>
    /// Every agent that has been given an identity there, with what the hub grants it — under this
    /// server's names, so the tool panel and this view agree about what a tool is called.
    /// </summary>
    [Fact]
    public async Task Agents_are_listed_with_what_the_hub_grants_them()
    {
        var (broker, _) = Build();
        await broker.SetGrantsAsync("dagger", ["hub__recipes__list", "hub__recipes__get"]);
        await broker.SetGrantsAsync("scout", []);

        var hub = Assert.Single(await broker.InspectHubsAsync());

        var dagger = hub.Agents.Single(a => a.Agent == "dagger");
        Assert.Equal(["hub__recipes__get", "hub__recipes__list"], dagger.Tools.Order(StringComparer.Ordinal));
        Assert.NotEmpty(dagger.UserId);
        Assert.True(dagger.IssuedAtUnixMs > 0);

        // Granted nothing is a real state, and a different one from having no identity at all.
        Assert.Empty(hub.Agents.Single(a => a.Agent == "scout").Tools);
    }

    /// <summary>
    /// Never put a key on the wire. This server holds one per agent so it can call as them; a copy
    /// in every admin client's memory is a copy nobody is tracking.
    /// </summary>
    [Fact]
    public async Task No_key_appears_anywhere_in_the_report()
    {
        var (broker, _) = Build();
        await broker.SetGrantsAsync("dagger", ["hub__recipes__list"]);
        var stored = await _identities.ForAgentAsync("hub", "dagger");

        var report = JsonSerializer.Serialize(await broker.InspectHubsAsync());

        Assert.DoesNotContain(stored!.Token, report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The state worth showing most: reachable, serving tools, and refusing to be managed. Nothing
    /// else on the page would look wrong, and every attempt to change an identity or a grant from
    /// here would fail.
    /// </summary>
    [Fact]
    public async Task A_hub_that_will_not_be_managed_is_reported_as_such()
    {
        var (broker, hub) = Build();
        hub.Refusing = true;

        var reported = Assert.Single(await broker.InspectHubsAsync());

        Assert.False(reported.Administrable);
    }
}
