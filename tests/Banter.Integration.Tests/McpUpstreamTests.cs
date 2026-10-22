using System.Runtime.CompilerServices;
using System.Text.Json;
using Banter.Agents.Sdk;
using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Banter.Server.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Integration.Tests;

/// <summary>
/// The tool path end to end against a REAL MCP server: an HTTP MCP endpoint in this process,
/// configured as an upstream the way mcp.json configures one, aggregated by MCPHub.Proxy inside
/// the Banter server, granted by an admin, and called by an agent over the Banter wire.
///
/// <para>Every other tool test here drives a fake broker or a fake hub, which proves the server's
/// rules and nothing about whether the broker can talk MCP to anything at all. This is the test
/// that answers "does it actually work".</para>
/// </summary>
public sealed class McpUpstreamTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("admin", "pw", isAgent: false, isAdmin: true)
        .AddUser("human", "pw")
        .AddUser("dagger", "pw", isAgent: true)
        .AddUser("scout", "pw", isAgent: true);

    private WebApplication _upstream = null!;
    private string _upstreamUrl = "";
    private readonly List<string> _upstreamCalls = [];
    private string _root = null!;
    private BanterDatabase _database = null!;
    private McpToolBroker _broker = null!;
    private BanterServer _server = null!;

    /// <summary>An MCP server with two tools, over the streamable HTTP transport MCPHub speaks.</summary>
    private async Task StartUpstreamAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer(o => o.ServerInfo = new Implementation { Name = "fixture", Version = "1.0" })
            .WithHttpTransport()
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    new Tool
                    {
                        Name = "echo",
                        Description = "Echoes the msg argument.",
                        InputSchema = JsonDocument.Parse("""{"type":"object","properties":{"msg":{"type":"string"}},"required":["msg"]}""").RootElement,
                    },
                    new Tool { Name = "boom", Description = "Always reports a tool error." },
                ],
            }))
            .WithCallToolHandler((context, _) =>
            {
                lock (_upstreamCalls)
                {
                    _upstreamCalls.Add(context.Params?.Name ?? "");
                }

                return ValueTask.FromResult(context.Params?.Name switch
                {
                    "echo" => new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = "echo:" + context.Params.Arguments?["msg"].GetString() }],
                    },
                    "boom" => new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "boom failed" }] },
                    _ => throw new InvalidOperationException("unexpected tool"),
                });
            });

        _upstream = builder.Build();
        _upstream.MapMcp("/mcp");
        await _upstream.StartAsync();
        var address = _upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _upstreamUrl = $"{address}/mcp";
    }

    public async Task InitializeAsync()
    {
        await StartUpstreamAsync();

        _root = Path.Combine(Path.GetTempPath(), $"banter-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();

        // Through the file, as a deployment is: what mcp.json says is what the server connects.
        var config = Path.Combine(_root, "mcp.json");
        await File.WriteAllTextAsync(config, $$"""
            { "upstreams": [ { "key": "fixture", "displayName": "Fixture", "url": "{{_upstreamUrl}}" } ] }
            """);
        _broker = await ToolHost.StartAsync(_database, config);

        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), files, tools: _broker);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _broker.DisposeAsync();
        await _upstream.StopAsync();
        await _upstream.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private Task<BanterClient> ConnectAsync(string user) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, "pw");

    [Fact]
    public async Task TheUpstreamConnectsAndItsToolsAreNamespacedUnderItsKey()
    {
        var upstream = Assert.Single(_broker.Upstreams);
        output.WriteLine($"{upstream.Key}: {upstream.State} {upstream.LastError}");
        Assert.Equal(MCPHub.Proxy.UpstreamState.Connected, upstream.State);

        await using var admin = await ConnectAsync("admin");
        var catalogue = (await admin.ListToolsAsync()).Tools;
        output.WriteLine(string.Join(", ", catalogue.Select(t => $"{t.Name} ({t.ServerKey})")));

        // Under the upstream's key, so two servers that both have an "echo" cannot collide, and
        // attributed to it, so the panel can say which server a grant reaches.
        Assert.Contains(catalogue, t => t.Name == "fixture__echo" && t.ServerKey == "fixture");
        Assert.Contains(catalogue, t => t.Name == "fixture__boom");
        Assert.Contains("msg", catalogue.Single(t => t.Name == "fixture__echo").Schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGrantedAgentCallsTheRealToolAndAnUngrantedOneCannot()
    {
        await using var admin = await ConnectAsync("admin");
        await using var dagger = await ConnectAsync("dagger");
        await using var scout = await ConnectAsync("scout");

        // Connecting a server grants nothing.
        Assert.Empty((await dagger.ListToolsAsync()).Tools);

        await admin.SetToolGrantsAsync("dagger", ["fixture__echo"]);

        Assert.Equal("fixture__echo", Assert.Single((await dagger.ListToolsAsync()).Tools).Name);
        var result = await dagger.CallToolAsync("fixture__echo", """{"msg":"hello from banter"}""");
        output.WriteLine($"{result.Name}: {result.Content} (error: {result.IsError})");
        Assert.False(result.IsError);
        Assert.Equal("echo:hello from banter", result.Content);

        // A tool the upstream reports as failed comes back as a failed result, not a crash.
        await admin.SetToolGrantsAsync("dagger", ["fixture__echo", "fixture__boom"]);
        var failed = await dagger.CallToolAsync("fixture__boom", "");
        Assert.True(failed.IsError);
        Assert.Equal("boom failed", failed.Content);

        // Another agent holds nothing, and its call never reaches the upstream.
        int before;
        lock (_upstreamCalls)
        {
            before = _upstreamCalls.Count;
        }

        var refused = await scout.CallToolAsync("fixture__echo", """{"msg":"x"}""");
        Assert.True(refused.IsError);
        lock (_upstreamCalls)
        {
            Assert.Equal(before, _upstreamCalls.Count);
        }
    }

    [Fact]
    public async Task ACallNamedInARoomIsAnnouncedThere()
    {
        await using var admin = await ConnectAsync("admin");
        await using var human = await ConnectAsync("human");
        await using var dagger = await ConnectAsync("dagger");
        await human.JoinAsync("#main");
        await dagger.JoinAsync("#main");
        await admin.SetToolGrantsAsync("dagger", ["fixture__echo"]);

        // Live, not from history: the server broadcasts a tool line to whoever is in the room and
        // does not store it.
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        human.MessageReceived += m =>
        {
            if (m.Text.StartsWith("[tool]", StringComparison.Ordinal))
            {
                seen.TrySetResult(m.Text);
            }
        };

        await dagger.CallToolAsync("fixture__echo", """{"msg":"audit me"}""", room: "#main");

        var line = await seen.Task.WaitAsync(Patience);
        output.WriteLine(line);
        Assert.Equal("[tool] dagger called 'fixture__echo' (fixture)", line);
    }

    /// <summary>An agent whose "model" calls whichever tools a test queues up.</summary>
    private sealed class ToolAgent(BanterAgentOptions options) : BanterAgent(options)
    {
        public Queue<(string Tool, string Args)> Plan { get; } = new();
        public List<ToolResultPayload> Results { get; } = [];

        public IReadOnlyList<string> ToolNamesIn(string room) => [.. ToolsFor(room).Select(t => t.Name)];

        public Task Refresh() => RefreshToolsAsync();

        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (Plan.Count > 0)
            {
                var (tool, args) = Plan.Dequeue();
                var result = await InvokeToolAsync(tool, args, room, cancellationToken).ConfigureAwait(false);
                lock (Results)
                {
                    Results.Add(result);
                }
            }

            yield return "ok";
        }
    }

    [Fact]
    public async Task AnAgentOnTheSdkIsOfferedTheToolAndItsTurnCallsIt()
    {
        await using var admin = await ConnectAsync("admin");
        await admin.SetToolGrantsAsync("dagger", ["fixture__echo"]);

        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");
        await using var agent = new ToolAgent(new BanterAgentOptions
        {
            Server = _server.Endpoint,
            User = "dagger",
            Password = "pw",
            Rooms = ["#main"],
            Locality = AgentLocality.Local,
            Clearance = DataSensitivity.Sensitive,
            Skills = ["chat"],
        });
        await agent.StartAsync(_transport);

        // In the same list as the SDK's own room tools, which is what a model is handed.
        Assert.Contains("fixture__echo", agent.ToolNamesIn("#main"));
        Assert.Contains("who_is_here", agent.ToolNamesIn("#main"));

        agent.Plan.Enqueue(("fixture__echo", """{"msg":"from a turn"}"""));
        await human.SendMessageAsync("#main", "@dagger go");

        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline && agent.Results.Count == 0)
        {
            await Task.Delay(25);
        }

        var result = Assert.Single(agent.Results);
        Assert.False(result.IsError);
        Assert.Equal("echo:from a turn", result.Content);
    }
}
