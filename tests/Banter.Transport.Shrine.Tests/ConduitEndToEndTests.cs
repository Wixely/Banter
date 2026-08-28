using Banter.Client.Core;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Banter.Transport.Shrine;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Nodestar;
using CupriNet.Vessel;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Transport.Shrine.Tests;

/// <summary>
/// A Banter server on a real CupriNet node, dialled by a real client over a conduit.
///
/// <para>Nothing had crossed this seam before — Nodestar's own note says its tests drive conduits
/// over an in-memory channel and no client opens one against a running node. So this is the first
/// exercise of the whole path: vessel, Pilgrimage, conduit, and BanterProtocol's own handshake and
/// verbs on top of it.</para>
/// </summary>
public sealed class ConduitEndToEndTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private NodestarApplication _node = null!;
    private ShrineBanterListener _listener = null!;
    private BanterServer _server = null!;
    private int _listenPort;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "banter-conduit-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);

        _database = new BanterDatabase(BanterStorageOptions.Parse(
            "sqlite", $"Data Source={Path.Combine(_root, "banter.db")}"));
        await _database.InitializeAsync();

        var accounts = new DbAccountStore(_database);
        await accounts.CreateUserAsync("alice", "pw", isAgent: false, isAdmin: true);
        await accounts.CreateUserAsync("bob", "pw", isAgent: false, isAdmin: false);

        _listenPort = FreePort();

        var builder = NodestarApplication.CreateBuilder([]);
        builder.Node.Concordium = "banter-conduit-test";
        builder.Node.DataDirectory = Path.Combine(_root, "mesh");
        builder.Node.ListenAddress = "127.0.0.1";
        builder.Node.ListenPort = _listenPort;
        builder.Node.SiteName = "Banter";
        builder.Node.Moniker = "banter-test-node";

        // The site's Sigil rides the link only when asked for. Without this a client is handed a
        // link to a node with nothing to say about what it hosts, and has nothing to make a
        // pilgrimage to.
        builder.Node.AdvertiseSiteInLink = true;

        // Nothing outward-facing: this is a loopback test, and a node that went looking for peers
        // would make it slow and dependent on the network it happened to run on.
        builder.Node.EnableWebRtc = false;
        builder.Node.EnableTor = false;
        builder.Node.EnableWebFront = false;
        builder.Node.EnableLanDiscovery = false;
        builder.Node.EnablePortMapping = false;

        _listener = builder.Site.ServeBanter(new Uri("cupri://banter-conduit-test/banter"));
        _node = builder.Build();

        _server = new BanterServer(
            new PreparedListenerTransport(_listener),
            accounts,
            new DbServerStore(_database),
            new FileStore(_database, new FileStoreOptions { DataDirectory = _root }));

        await _server.StartAsync(_listener.LocalEndpoint);
        await _node.StartAsync();

        output.WriteLine($"site: {_node.SiteAddress}");
        output.WriteLine($"node listening on 127.0.0.1:{_listenPort}");
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// A client transport for this test's site. No node on this side: a Pilgrim needs only a
    /// vessel, which is the property that lets a browser be one. The link carries the site's Sigil
    /// and network, so nothing else has to be told to it.
    /// </summary>
    private ShrineClientTransport ClientTransport() =>
        new(async (_, ct) => await TcpVessel.ConnectAsync("127.0.0.1", _listenPort, cancellationToken: ct),
            new BouncyCastleSuite());

    /// <summary>The node's signed link, which is what a real client would be handed.</summary>
    private Uri Link() => new(new NodestarLinkProvider(
        _node.Node, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)).Current().Link);

    [Fact(Skip = "Blocked on CupriNodestar#2: a conduit opened over a TCP vessel is closed as soon as the pilgrimage completes, and the site's OnSession handler is never invoked. The test is the reproduction — unskip when the conduit is routed on that path.")]
    public async Task AClientReachesTheServerOverAConduit()
    {
        await using var client = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "alice", "pw")
            .WaitAsync(Patience);

        // The handshake completed, which means BanterProtocol's own framing rode the conduit.
        Assert.Equal("alice", client.Nick);
    }

    [Fact(Skip = "Blocked on CupriNodestar#2: a conduit opened over a TCP vessel is closed as soon as the pilgrimage completes, and the site's OnSession handler is never invoked. The test is the reproduction — unskip when the conduit is routed on that path.")]
    public async Task TwoClientsTalkToEachOtherThroughTheSite()
    {
        await using var alice = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "alice", "pw")
            .WaitAsync(Patience);
        await using var bob = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "bob", "pw")
            .WaitAsync(Patience);

        var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.MessageReceived += m =>
        {
            if (m.Sender == "alice")
            {
                heard.TrySetResult(m.Text);
            }
        };

        await alice.JoinAsync("#main");
        await bob.JoinAsync("#main");
        await alice.SendMessageAsync("#main", "hello over L2");

        Assert.Equal("hello over L2", await heard.Task.WaitAsync(Patience));
    }

    [Fact(Skip = "Blocked on CupriNodestar#2: a conduit opened over a TCP vessel is closed as soon as the pilgrimage completes, and the site's OnSession handler is never invoked. The test is the reproduction — unskip when the conduit is routed on that path.")]
    public async Task HistoryComesBackOverTheConduit()
    {
        await using var alice = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "alice", "pw")
            .WaitAsync(Patience);

        await alice.JoinAsync("#history");
        await alice.SendMessageAsync("#history", "written over a conduit");

        await using var bob = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "bob", "pw")
            .WaitAsync(Patience);
        await bob.JoinAsync("#history");

        var page = await bob.GetHistoryAsync("#history", limit: 50).WaitAsync(Patience);

        Assert.Contains(page.Messages, m => m.Text == "written over a conduit");
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _listener.DisposeAsync();
        await _node.DisposeAsync();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A node's files can outlive the test on Windows; the temp directory is not the point.
        }
    }
}
