using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Banter.Transport.CupriNet;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>
/// The PLAN Phase 0 CupriNet spike: pair (Conjoin against the server's intonation link) →
/// authenticated channel (Consecrate with the watchword) → bidirectional Banter frames over
/// Conduits → full chat through the real server. Windows↔Windows loopback here; on-device
/// Android remains a manual spike item.
/// </summary>
public sealed class CupriNetTransportSpikeTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string Watchword = "banter-spike-secret";

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;
    private CupriNetBanterTransport _serverTransport = null!;
    private readonly List<CupriNetBanterTransport> _clientTransports = [];

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-cupri-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();

        _serverTransport = new CupriNetBanterTransport(new CupriNetTransportOptions
        {
            DataDirectory = Path.Combine(_root, "server-node"),
            Watchword = Watchword,
        });
        var accounts = new InMemoryAccountStore().AddUser("alice", "pw-a").AddUser("bob", "pw-b");
        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(_serverTransport, accounts, new DbServerStore(_database), files);
        await _server.StartAsync(new Uri("cuprinet://listen")).WaitAsync(Timeout);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        foreach (var transport in _clientTransports)
        {
            await transport.DisposeAsync();
        }

        await _serverTransport.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Node file handles can outlive disposal briefly; temp cleanup is best-effort.
        }
    }

    private CupriNetBanterTransport NewClientTransport(string name, string watchword = Watchword)
    {
        var transport = new CupriNetBanterTransport(new CupriNetTransportOptions
        {
            DataDirectory = Path.Combine(_root, $"client-node-{name}"),
            Watchword = watchword,
        });
        _clientTransports.Add(transport);
        return transport;
    }

    [Fact]
    public async Task ServerPublishesAnIntonationLink()
    {
        var endpoint = _server.Endpoint;
        Assert.Equal("cuprinet", endpoint.Scheme);
        Assert.Contains("intone", endpoint.OriginalString);
    }

    [Fact]
    public async Task ChatFlowsEndToEndOverCupriNet()
    {
        var aliceClient = await BanterClient.ConnectAsync(
            NewClientTransport("alice"), _server.Endpoint, "alice", "pw-a").WaitAsync(Timeout);
        await using var alice = aliceClient;
        var bobClient = await BanterClient.ConnectAsync(
            NewClientTransport("bob"), _server.Endpoint, "bob", "pw-b").WaitAsync(Timeout);
        await using var bob = bobClient;

        var bobSees = new TaskCompletionSource<MsgPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.MessageReceived += m => bobSees.TrySetResult(m);

        await alice.JoinAsync("#mesh");
        await bob.JoinAsync("#mesh");
        await alice.SendMessageAsync("#mesh", "hello over the mesh");

        var seen = await bobSees.Task.WaitAsync(Timeout);
        Assert.Equal("alice", seen.Sender);
        Assert.Equal("hello over the mesh", seen.Text);
        Assert.False(string.IsNullOrEmpty(seen.MessageId));

        // History replay round-trips over the same channel.
        var history = await bob.GetHistoryAsync("#mesh");
        Assert.Contains(history.Messages, m => m.Text == "hello over the mesh");

        // And the ping round-trip gives us a latency number for the spike report.
        var rtt = await alice.PingAsync();
        Assert.True(rtt < Timeout);
    }

    [Fact]
    public async Task WrongWatchwordCannotConsecrate()
    {
        var wrong = NewClientTransport("mallory", watchword: "not-the-secret");
        // Either the handshake fails outright or it never completes — both are a refusal;
        // WaitAsync turns "never completes" into TimeoutException.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            BanterClient.ConnectAsync(wrong, _server.Endpoint, "alice", "pw-a").WaitAsync(TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public async Task PeerDisposalEndsTheSessionCleanly()
    {
        var transport = NewClientTransport("dropper");
        var connection = await transport.ConnectAsync(_server.Endpoint).WaitAsync(Timeout);
        await connection.DisposeAsync();
        // The server side notices and tears the session down without taking the server out —
        // proven by a fresh client still getting through afterwards.
        var check = await BanterClient.ConnectAsync(
            NewClientTransport("checker"), _server.Endpoint, "alice", "pw-a").WaitAsync(Timeout);
        await check.DisposeAsync();
    }
}
