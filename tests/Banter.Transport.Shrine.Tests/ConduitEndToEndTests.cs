using System.Net;
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
///
/// <para>The client dials the <b>site's</b> vessel host, not the node's beacon port, and pins the
/// site's Signet. Those are the two halves of CupriNodestar#2: the node's port reaches the node,
/// and a session with no Shrine behind it answers every rite with a closed stream.</para>
/// </summary>
public sealed class ConduitEndToEndTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private NodestarApplication _node = null!;
    private ShrineBanterListener _listener = null!;
    private ShrineVesselHost _host = null!;
    private BanterServer _server = null!;

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

        var builder = NodestarApplication.CreateBuilder([]);
        builder.Node.Concordium = "banter-conduit-test";
        builder.Node.DataDirectory = Path.Combine(_root, "mesh");
        builder.Node.ListenAddress = "127.0.0.1";
        builder.Node.ListenPort = FreePort();
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

        // The site's own front door. Distinct from the node's beacon port on purpose: a vessel
        // accepted here is served as the site, and one accepted there is not.
        _host = new ShrineVesselHost(_node, new IPEndPoint(IPAddress.Loopback, 0));
        _host.Start();

        output.WriteLine($"site: {_node.SiteAddress}");
        output.WriteLine($"site listening on {_host.LocalEndPoint}");
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
        new(async (_, ct) => await TcpVessel.ConnectAsync(
                "127.0.0.1", _host.LocalEndPoint.Port, cancellationToken: ct),
            new BouncyCastleSuite());

    /// <summary>The node's signed link, which is what a real client would be handed.</summary>
    private Uri Link() => new(new NodestarLinkProvider(
        _node.Node, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)).Current().Link);

    /// <summary>
    /// Closing a connection twice does nothing the second time.
    ///
    /// <para>This exists because the code it guards was deleted. Disposing a session whose far end
    /// had already gone used to throw rather than doing nothing (CupriNodestar#3), so the transport
    /// swallowed <see cref="ObjectDisposedException"/> at its connection seam — which also made
    /// "already closed" indistinguishable from a real disposal fault anywhere beneath it. CupriNet
    /// 0.6.0 made dispose idempotent and the catch is gone; if that ever regresses, the exception
    /// now reaches here instead of being quietly absorbed in production.</para>
    /// </summary>
    [Fact]
    public async Task ClosingAConnectionTwiceIsNotAnError()
    {
        var client = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "alice", "pw")
            .WaitAsync(Patience);

        await client.JoinAsync("#twice");

        await client.DisposeAsync().AsTask().WaitAsync(Patience);
        await client.DisposeAsync().AsTask().WaitAsync(Patience);
    }

    [Fact]
    public async Task AClientReachesTheServerOverAConduit()
    {
        await using var client = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "alice", "pw")
            .WaitAsync(Patience);

        // The handshake completed, which means BanterProtocol's own framing rode the conduit.
        Assert.Equal("alice", client.Nick);
    }

    [Fact]
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

    [Fact]
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
        await _host.DisposeAsync();
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
