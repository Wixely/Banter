using System.Runtime.CompilerServices;
using Banter.Agents.Sdk;
using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Persistence;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>
/// What survives a server restart on the AGENT layer. The client already redials, re-auths and
/// rejoins (ChatIntegrationTests proves that); but the announced attributes live on the server
/// session that died with the old connection, so an agent that does not re-announce comes back
/// with Unknown locality and clearance — still in its rooms, still answering, but treated as
/// frontier and uncleared, and so no longer electable or routable. Silently.
/// </summary>
public sealed class AgentReconnectTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw")
        .AddUser("scout", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private Banter.Server.Files.FileStore _files = null!;
    private BanterServer _server = null!;

    private sealed class EchoAgent(BanterAgentOptions options) : BanterAgent(options)
    {
        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return "echo";
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-agent-reconnect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        _files = new Banter.Server.Files.FileStore(
            _database, new Banter.Server.Files.FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), _files);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AnAgentKeepsItsAttributesAcrossAServerRestart()
    {
        var port = _server.Endpoint.Port;

        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var agent = new EchoAgent(new BanterAgentOptions
        {
            Server = _server.Endpoint,
            User = "scout",
            Password = "pw",
            Rooms = ["#main"],
            Locality = AgentLocality.Local,
            Clearance = DataSensitivity.Sensitive,
            Skills = ["chat"],
        });
        await agent.StartAsync(_transport);

        // The announced attributes are on file: a Local agent gets elected, an Unknown one never is.
        await WaitForRosterAsync(human, a => a.Locality == AgentLocality.Local && a.IsDelegator,
            "scout to be elected with Local locality");

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        human.Reconnected += () => reconnected.TrySetResult();

        // Same stores, same port, new server: every session — and every announcement held on
        // one — is gone.
        await _server.DisposeAsync();
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), _files);
        await StartOnPortWithRetryAsync(_server, port);
        await reconnected.Task.WaitAsync(Timeout);

        // The point of the test: after its own reconnect the agent re-announces, so the roster
        // shows Local again and the election re-picks it. Without the re-announce it would sit
        // at Unknown — present, answering, and quietly ineligible.
        await WaitForRosterAsync(human, a => a.Locality == AgentLocality.Local && a.IsDelegator,
            "scout to be re-elected with Local locality after the restart");
    }

    /// <summary>Polls the roster until scout matches, riding out the observer's own reconnect.</summary>
    private static async Task WaitForRosterAsync(
        BanterClient observer, Func<AgentInfoPayload, bool> predicate, string what)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        AgentInfoPayload? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var agents = await observer.GetAgentsAsync("#main");
                last = agents.Agents.FirstOrDefault(a =>
                    string.Equals(a.Nick, "scout", StringComparison.OrdinalIgnoreCase));
                if (last is not null && predicate(last))
                {
                    return;
                }
            }
            catch (BanterClientException)
            {
                // Mid-reconnect; try again.
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting for {what}. Last saw: " +
                    (last is null ? "(scout not in roster)" : $"locality={last.Locality}, delegator={last.IsDelegator}"));
    }

    private static async Task StartOnPortWithRetryAsync(BanterServer server, int port)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await server.StartAsync(new Uri($"tcp://127.0.0.1:{port}"));
                return;
            }
            catch (System.Net.Sockets.SocketException) when (attempt < 20)
            {
                await Task.Delay(100);
            }
        }
    }
}
