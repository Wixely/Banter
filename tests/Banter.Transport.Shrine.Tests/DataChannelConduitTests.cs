using System.Net;
using Banter.Client.Core;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Nodestar;
using CupriNet.Vessel;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Transport.Shrine.Tests;

/// <summary>
/// Banter over a <see cref="DataChannelVessel"/> — the vessel a browser will hand the Pilgrimage.
///
/// <para>The conduit-over-WebRTC path is the one thing Nodestar says is still unproven: only an
/// accepted DataChannel routes into the Pilgrimage on its own, its reference client opens no
/// conduit, and so nothing has ever carried a conduit that way. A browser is not available here,
/// but the vessel it would use is — and the half worth testing without one is the framing, because
/// a DataChannel preserves message boundaries where TCP coalesces.</para>
///
/// <para>What this does <b>not</b> prove: ICE, the browser's own channel, or NativeAOT-LLVM. Those
/// wait for the web head. What it does prove is that nothing above the vessel depends on stream
/// semantics.</para>
/// </summary>
public sealed class DataChannelConduitTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private NodestarApplication _node = null!;
    private ShrineBanterListener _listener = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "banter-dc-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);

        _database = new BanterDatabase(BanterStorageOptions.Parse(
            "sqlite", $"Data Source={Path.Combine(_root, "banter.db")}"));
        await _database.InitializeAsync();

        var accounts = new DbAccountStore(_database);
        await accounts.CreateUserAsync("alice", "pw", isAgent: false, isAdmin: true);
        await accounts.CreateUserAsync("bob", "pw", isAgent: false, isAdmin: false);

        var builder = NodestarApplication.CreateBuilder([]);
        builder.Node.Concordium = "banter-datachannel-test";
        builder.Node.DataDirectory = Path.Combine(_root, "mesh");
        builder.Node.ListenAddress = "127.0.0.1";
        builder.Node.ListenPort = FreePort();
        builder.Node.SiteName = "Banter";
        builder.Node.Moniker = "banter-dc-node";
        builder.Node.AdvertiseSiteInLink = true;
        builder.Node.EnableWebRtc = false;
        builder.Node.EnableTor = false;
        builder.Node.EnableWebFront = false;
        builder.Node.EnableLanDiscovery = false;
        builder.Node.EnablePortMapping = false;

        _listener = builder.Site.ServeBanter(new Uri("cupri://banter-datachannel-test/banter"));
        _node = builder.Build();

        _server = new BanterServer(
            new PreparedListenerTransport(_listener),
            accounts,
            new DbServerStore(_database),
            new FileStore(_database, new FileStoreOptions { DataDirectory = _root }));

        await _server.StartAsync(_listener.LocalEndpoint);
        await _node.StartAsync();

        output.WriteLine($"site: {_node.SiteAddress}");
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>The node's signed link, which is what a real client would be handed.</summary>
    private Uri Link() => new(new NodestarLinkProvider(
        _node.Node, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)).Current().Link);

    /// <summary>
    /// The browser's whole job, and all of it: make a channel, wrap it, let the site accept the
    /// other end. Everything above this — Pilgrimage, conduit, BanterProtocol, the UI — is the code
    /// that already runs on the desktop.
    /// </summary>
    private readonly List<LoopbackDataChannel> _clientChannels = [];

    private ShrineClientTransport ClientTransport() => new(
        (_, _) =>
        {
            var (clientEnd, siteEnd) = LoopbackDataChannel.Pair();
            lock (_clientChannels)
            {
                _clientChannels.Add(clientEnd);
            }

            // The site accepts its end exactly as it would accept a DataChannel arriving from a
            // browser. Not awaited: the pilgrimage lasts as long as the visitor does.
            _ = Task.Run(() => _node.AcceptPilgrimageAsync(new DataChannelVessel(siteEnd)));

            return Task.FromResult<IVessel>(new DataChannelVessel(clientEnd));
        },
        new BouncyCastleSuite());

    [Fact]
    public async Task AClientReachesTheServerOverADataChannel()
    {
        await using var client = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "alice", "pw")
            .WaitAsync(Patience);

        Assert.Equal("alice", client.Nick);
    }

    [Fact]
    public async Task TwoClientsTalkToEachOtherOverDataChannels()
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
        await alice.SendMessageAsync("#main", "hello from a data channel");

        Assert.Equal("hello from a data channel", await heard.Task.WaitAsync(Patience));
    }

    /// <summary>A long message round-trips over a message-oriented vessel.</summary>
    [Fact]
    public async Task ALongMessageSurvivesTheMessageBoundaries()
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

        await alice.JoinAsync("#long");
        await bob.JoinAsync("#long");

        var wall = new string('x', 60_000);
        await alice.SendMessageAsync("#long", wall);

        Assert.Equal(wall, await heard.Task.WaitAsync(Patience));
    }

    /// <summary>
    /// <b>A characterisation test, not a wish.</b> <c>DataChannelVessel</c> emits one channel
    /// message per frame and never fragments, at any size — so a frame at the ceiling
    /// <c>Conduits.MaxPayloadBytes</c> advertises (196608) becomes a single ~197 KB
    /// <c>RTCDataChannel.send</c>.
    ///
    /// <para>That matters because a browser's SCTP transport has a maximum message size of its own.
    /// 256 KiB is reachable where both ends negotiate EOR, but 64 KiB is the interoperable floor and
    /// 16 KiB the long-standing safe chunk; over it, <c>send</c> throws or the channel closes. So
    /// the ceiling the rite reports is not one this path can carry, and the fragmenting has to
    /// happen somewhere — for now, in Banter's own <see cref="IDataChannel"/> implementation when
    /// the web head has one (CupriNodestar#4).</para>
    ///
    /// <para>Pinned here so the web head inherits the constraint as a failing test rather than as a
    /// browser console error. If upstream starts fragmenting, this fails and says so.</para>
    /// </summary>
    [Fact]
    public async Task AFrameBecomesExactlyOneDataChannelMessage()
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

        await alice.JoinAsync("#big");
        await bob.JoinAsync("#big");

        var wall = new string('x', 150_000);
        await alice.SendMessageAsync("#big", wall);
        Assert.Equal(wall, await heard.Task.WaitAsync(Patience));

        LoopbackDataChannel[] channels;
        lock (_clientChannels)
        {
            channels = [.. _clientChannels];
        }

        var largest = channels.Max(c => c.LargestMessageSent);
        output.WriteLine($"largest single channel message: {largest} bytes for a {wall.Length}-byte message");

        Assert.True(
            largest >= wall.Length,
            $"the vessel fragmented ({largest} < {wall.Length}) — upstream may have fixed " +
            "CupriNodestar#4, in which case the web head no longer needs to chunk");
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
