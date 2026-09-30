using System.Net;
using Banter.Protocol.Transport;
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
    private FileStore _files = null!;
    private FileRelics _relics = null!;
    private FlakyRelics _flaky = null!;

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

        // Files as relics (PLAN §2.5), which is the same act as ServeBanter and a separate rite:
        // the conduit answers "may I", the Relic stream carries the bytes.
        _files = new FileStore(_database, new FileStoreOptions { DataDirectory = _root });
        _relics = new FileRelics(_files);
        _flaky = new FlakyRelics(_relics);
        builder.Site.ServeBanterRelics(_flaky);

        _node = builder.Build();

        _server = new BanterServer(
            new PreparedListenerTransport(_listener),
            accounts,
            new DbServerStore(_database),
            _files,
            relics: _relics);

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

    /// <summary>
    /// A room file crosses as a relic: ticket on the conduit, bytes on the Relic stream, every chunk
    /// verified against a manifest on the way (PLAN §2.5).
    ///
    /// <para>Bigger than one chunk on purpose. A 192 KiB chunk size means a single-chunk file proves
    /// only that one buffer arrived — it would pass with the chunk index ignored, with the manifest's
    /// hash list unread, and with a transfer that cannot resume. Three chunks is where ordering,
    /// per-chunk hashing and the final whole-file check all have something to be wrong about.</para>
    ///
    /// <para>The fetch is driven through the transport's own <see cref="IBanterRelicFetch"/> rather
    /// than through <c>DownloadFileAsync</c>, because this test is about the rite: if the manifest,
    /// the chunk hashes or the whole-file hash disagreed with the bytes, the fetch throws instead of
    /// returning something that merely looks right.</para>
    /// </summary>
    [Fact]
    public async Task AFileCrossesAsAVerifiedRelic()
    {
        await using var alice = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "alice", "pw")
            .WaitAsync(Patience);

        await alice.JoinAsync("#relics");
        var content = Noise((192 * 1024 * 2) + 7_777);
        var stored = await alice
            .UploadFileAsync("#relics", "noise.bin", content, "application/octet-stream")
            .WaitAsync(Patience);

        var ticket = await alice.RequestRelicAsync(stored.FileId).WaitAsync(Patience);
        output.WriteLine($"relic {ticket.RelicName} is {ticket.Length} bytes, good until {ticket.ExpiresAt}");

        // The name is not the file id, and not derived from anything the client chose.
        Assert.NotEqual(stored.FileId, ticket.RelicName);
        Assert.DoesNotContain(stored.FileId, ticket.RelicName);
        Assert.Equal(content.Length, ticket.Length);

        var fetch = Assert.IsAssignableFrom<IBanterRelicFetch>(alice.RelicFetch);
        var fetched = await fetch.FetchRelicAsync(ticket.RelicName, ticket.Length).WaitAsync(Patience);

        Assert.Equal(content, fetched);
    }

    /// <summary>
    /// <c>DownloadFileAsync</c> takes the relic path here, and the bytes are the bytes.
    ///
    /// <para>Falling back is silent by design — the download still completes over FILE_GET — so a
    /// relic path that had quietly stopped working would leave every other test in this file
    /// passing. Three facts together are what close that off, and none of them alone does: the
    /// transport offers the rite, the server will mint for this file, and no fallback fired. Without
    /// the middle one a server built with no relic source answers <c>NO_RELICS</c>, takes the frame
    /// path, raises nothing — and this test would pass having proven the fallback works.</para>
    /// </summary>
    [Fact]
    public async Task DownloadingTakesTheRelicPathWithoutFallingBack()
    {
        await using var alice = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "alice", "pw")
            .WaitAsync(Patience);

        var fellBack = new List<Exception>();
        alice.RelicFetchFailed += (_, exception) => fellBack.Add(exception);

        await alice.JoinAsync("#download");
        var content = Noise(300_000);
        var stored = await alice
            .UploadFileAsync("#download", "download.bin", content, "application/octet-stream")
            .WaitAsync(Patience);

        Assert.NotNull(alice.RelicFetch);
        var ticket = await alice.RequestRelicAsync(stored.FileId).WaitAsync(Patience);
        Assert.NotEmpty(ticket.RelicName);

        var fetched = await alice.DownloadFileAsync(stored.FileId).WaitAsync(Patience);

        Assert.Equal(content, fetched);
        Assert.True(fellBack.Count == 0, string.Join("; ", fellBack.Select(e => e.Message)));

        // And the download did not mint a second time. A ticket costs a pass over the file to hash
        // it, so a room of people opening one attachment must not pay for it each: a live ticket
        // with life left in it is handed back.
        var again = await alice.RequestRelicAsync(stored.FileId).WaitAsync(Patience);
        Assert.Equal(ticket.RelicName, again.RelicName);
    }

    /// <summary>
    /// A relic name is the authority, and it is not handed out to someone who may not read the file.
    ///
    /// <para>This is the whole reason tickets exist rather than relics being named after file ids.
    /// The rite is served by a node-wide source that is given a name and told nothing about who is
    /// asking — no account, no session, no rooms — so if the name were derivable, completing a
    /// handshake would be enough to read every room's files. Two halves, and both have to hold: the
    /// conduit refuses to mint for a session with no access, and the rite refuses a name nobody
    /// minted.</para>
    /// </summary>
    [Fact]
    public async Task ARelicIsRefusedToSomeoneWhoMayNotReadTheFile()
    {
        await using var alice = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "alice", "pw")
            .WaitAsync(Patience);
        await alice.JoinAsync("#private");
        var stored = await alice
            .UploadFileAsync("#private", "secret.bin", Noise(1_000), "application/octet-stream")
            .WaitAsync(Patience);

        // Bob is on the same site, authenticated, holding a conduit — and not in that room.
        await using var bob = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "bob", "pw")
            .WaitAsync(Patience);
        await bob.JoinAsync("#elsewhere");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(
            () => bob.RequestRelicAsync(stored.FileId).WaitAsync(Patience));
        Assert.Equal("NO_ACCESS", refused.Code);

        // Which leaves guessing, against 256 bits. A name nobody minted resolves to nothing, and
        // the rite says the same thing for that as for one that has expired.
        var guessed = new string('a', 64) + "/secret.bin";
        await Assert.ThrowsAsync<CupriNet.Rites.RelicException>(
            () => bob.RelicFetch!.FetchRelicAsync(guessed, 1_000).WaitAsync(Patience));
    }

    /// <summary>
    /// A relic that fails on the rite still comes back over the frame pipe.
    ///
    /// <para>The fallback is why <c>DownloadFileAsync</c> can prefer relics at all without the
    /// preference being a risk: whatever the rite does — a name that expired mid-transfer, a node
    /// that stopped serving relics, a chunk that will not verify — the file is still reachable the
    /// way it always was.</para>
    ///
    /// <para>Broken at the chunk rather than at the ticket, and that is not a detail. Expiring a
    /// ticket proves nothing here: the download asks for its own, is minted a fresh one, and takes
    /// the relic path as if nothing had happened. The failure that has to be survived is the one
    /// that arrives <i>during</i> a transfer, after the manifest was published and believed.</para>
    /// </summary>
    [Fact]
    public async Task AnUnusableRelicFallsBackToTheFramePipe()
    {
        await using var alice = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "alice", "pw")
            .WaitAsync(Patience);

        var fellBack = new List<Exception>();
        alice.RelicFetchFailed += (_, exception) => fellBack.Add(exception);

        await alice.JoinAsync("#fallback");
        var content = Noise(250_000);
        var stored = await alice
            .UploadFileAsync("#fallback", "fallback.bin", content, "application/octet-stream")
            .WaitAsync(Patience);

        _flaky.ChunksMissing = true;
        var fetched = await alice.DownloadFileAsync(stored.FileId).WaitAsync(Patience);

        Assert.Equal(content, fetched);

        // It fell back rather than succeeding by another route, and said so. A silent fallback is
        // the failure mode this event exists for: a deployment on the slow path for every file,
        // with nothing anywhere to say why.
        Assert.Single(fellBack);
        output.WriteLine($"fell back: {fellBack[0].Message}");
    }

    /// <summary>
    /// Revoking a grant stops a name that was already handed out.
    ///
    /// <para>The name is a bearer capability, so the only question that matters about it is how long
    /// it outlives the permission it was minted under. Its lifetime is the backstop; this is the
    /// mechanism. Revoke is the case that would otherwise be worst — the blob is untouched, so a
    /// stale name keeps working perfectly for minutes after someone decided it should not.</para>
    ///
    /// <para>Read back through the rite rather than through the source, because that is the thing an
    /// unauthorised holder would actually use.</para>
    /// </summary>
    [Fact]
    public async Task RevokingAGrantStopsANameAlreadyHandedOut()
    {
        await using var alice = await BanterClient
            .ConnectAsync(ClientTransport(), Link(), "alice", "pw")
            .WaitAsync(Patience);

        await alice.JoinAsync("#revoked");
        var stored = await alice
            .UploadFileAsync("#revoked", "recall.bin", Noise(2_000), "application/octet-stream")
            .WaitAsync(Patience);

        var ticket = await alice.RequestRelicAsync(stored.FileId).WaitAsync(Patience);
        Assert.NotEmpty(await alice.RelicFetch!.FetchRelicAsync(ticket.RelicName, ticket.Length).WaitAsync(Patience));

        await alice.RevokeFileAsync(stored.FileId, "#revoked").WaitAsync(Patience);

        // The same name, moments later, resolves to nothing at all.
        await Assert.ThrowsAsync<CupriNet.Rites.RelicException>(
            () => alice.RelicFetch!.FetchRelicAsync(ticket.RelicName, ticket.Length).WaitAsync(Patience));
    }

    /// <summary>
    /// A relic source that can be made to lose its chunks, standing in for the node that stopped
    /// serving them. It resolves manifests throughout — which is the case worth covering, because a
    /// published manifest is a promise the fetcher has already accepted.
    /// </summary>
    private sealed class FlakyRelics(IBanterRelicSource inner) : IBanterRelicSource
    {
        public bool ChunksMissing { get; set; }

        public BanterRelic? Find(string relicName) => inner.Find(relicName);

        public Task<byte[]?> ReadChunkAsync(
            string relicName,
            int chunkIndex,
            CancellationToken cancellationToken = default) =>
            ChunksMissing
                ? Task.FromResult<byte[]?>(null)
                : inner.ReadChunkAsync(relicName, chunkIndex, cancellationToken);
    }

    /// <summary>
    /// Content that is not compressible and not repetitive, so a chunk landing in the wrong order
    /// or a chunk boundary off by a byte cannot pass a hash check by luck.
    /// </summary>
    private static byte[] Noise(int length)
    {
        var bytes = new byte[length];
        new Random(1234).NextBytes(bytes);
        return bytes;
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
