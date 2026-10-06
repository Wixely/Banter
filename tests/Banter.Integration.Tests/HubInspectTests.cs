using System.Text.Json;
using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Banter.Server.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Integration.Tests;

/// <summary>
/// HUB_INSPECT over the wire: the hubs page asking a running server what it defers to.
///
/// <para>The broker's own answer is covered in Banter.Server.Tests. What is only true here is the
/// round trip - that the reply deserialises as a report rather than throwing, that a non-admin is
/// refused rather than handed a map of the estate, and that a server with no tool backend says so
/// instead of answering with an empty list. The page distinguishes those two (PLAN 8c), so the
/// difference has to be real on the wire and not merely in the client.</para>
/// </summary>
public sealed class HubInspectTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();

    private string _root = null!;
    private BanterDatabase _database = null!;
    private DbAccountStore _accounts = null!;
    private McpToolBroker? _broker;
    private BanterServer _server = null!;

    /// <summary>A hub that answers the management calls the broker makes while provisioning, so
    /// an agent can be given an identity without an MCPHub to talk to.</summary>
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

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Started per test rather than in InitializeAsync, because one of these needs a server with
    /// NO tool backend and that is a different server, not a different call.
    /// </summary>
    private async Task StartAsync(bool withTools)
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-hubs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();

        _accounts = new DbAccountStore(_database);
        await _accounts.CreateUserAsync("root", "pw", isAdmin: true);
        await _accounts.CreateUserAsync("nell", "pw");

        if (withTools)
        {
            var options = new McpOptions
            {
                Upstreams =
                [
                    new McpUpstreamConfig
                    {
                        // A URL nothing is listening on: the hub is unreachable for TOOLS, which
                        // is the state the page most needs to report, while the provisioning
                        // calls still go through the injected admin.
                        Key = "hub", DisplayName = "MCPHub",
                        Url = "http://127.0.0.1:1/mcp", PerAgentIdentity = true,
                    },
                ],
            };

            var hub = new FakeHub();
            _broker = new McpToolBroker(
                options, new ToolGrantStore(_database), new HubIdentityStore(_database),
                loggerFactory: null, hubAdmin: _ => hub);
            await _broker.SetGrantsAsync("scribe", ["hub__recipes__list", "hub__recipes__get"]);
        }

        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(
            _transport, _accounts, new DbServerStore(_database), files,
            tasks: new TaskStore(_database),
            tools: _broker,
            identities: new AgentIdentityStore(_database),
            accountAdmin: _accounts);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        if (_broker is not null)
        {
            await _broker.DisposeAsync();
        }

        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private Task<BanterClient> ConnectAsync(string user, string pass) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, pass);

    [Fact]
    public async Task AnAdminGetsTheHubsAndWhatEachAgentHoldsOnThem()
    {
        await StartAsync(withTools: true);
        await using var admin = await ConnectAsync("root", "pw");

        var hubs = await admin.InspectHubsAsync().WaitAsync(Patience);
        var hub = Assert.Single(hubs);
        output.WriteLine($"{hub.Key} ({hub.DisplayName}): {hub.State}, {hub.ToolCount} tools, "
            + $"administrable={hub.Administrable}, agents={hub.Agents.Count}");

        Assert.Equal("hub", hub.Key);
        Assert.Equal("MCPHub", hub.DisplayName);

        var agent = Assert.Single(hub.Agents);
        output.WriteLine($"  {agent.Agent} is {agent.UserId}: {string.Join(", ", agent.Tools)}");
        Assert.Equal("scribe", agent.Agent);
        Assert.Equal(["hub__recipes__get", "hub__recipes__list"], agent.Tools.Order(StringComparer.Ordinal));

        // The key the hub minted stays on the server. Checked against the serialised REPLY rather
        // than the payload type, because this is the hop where a leak would actually happen.
        Assert.DoesNotContain("mcphub_k", JsonSerializer.Serialize(hubs), StringComparison.Ordinal);
    }

    /// <summary>
    /// It names every agent that has an identity on a hub and what each holds, which is a map of
    /// the estate - so it is admin-only, and refused rather than quietly emptied.
    /// </summary>
    [Fact]
    public async Task AMemberIsRefused()
    {
        await StartAsync(withTools: true);
        await using var member = await ConnectAsync("nell", "pw");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(
            () => member.InspectHubsAsync().WaitAsync(Patience));

        output.WriteLine($"{refused.Code}: {refused.Message}");
        Assert.Equal("NOT_ADMIN", refused.Code);
    }

    /// <summary>
    /// "No hubs" and "I could not ask" must not look the same: the first is a deployment choice
    /// and the second is something to fix. The page puts the refusal where the count goes.
    /// </summary>
    [Fact]
    public async Task AServerWithNoToolBackendRefusesRatherThanReportingNothing()
    {
        await StartAsync(withTools: false);
        await using var admin = await ConnectAsync("root", "pw");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(
            () => admin.InspectHubsAsync().WaitAsync(Patience));

        output.WriteLine($"{refused.Code}: {refused.Message}");
        Assert.Equal("NO_TOOLS", refused.Code);
    }
}
