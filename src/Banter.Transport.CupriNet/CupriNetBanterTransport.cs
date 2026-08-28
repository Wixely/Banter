using System.Net;
using System.Security.Cryptography;
using System.Text;
using Banter.Protocol.Transport;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Arcanum;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Persistence;
using CupriNet.Rites;

namespace Banter.Transport.CupriNet;

public sealed record CupriNetTransportOptions
{
    /// <summary>Where this node keeps its master key, secrets, and peer caches. One directory
    /// per node identity.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>The server password / invite secret (PLAN §3). Both sides must agree; it is
    /// hashed into the CupriNet watchword that gates channel Consecration.</summary>
    public required string Watchword { get; init; }

    /// <summary>Overlay namespace. Banter nodes only gossip with Banter nodes.</summary>
    public string NetworkId { get; init; } = "banter";

    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;
    public int ListenPort { get; init; }
    public bool EnableLanDiscovery { get; init; }
    public bool EnablePortMapping { get; init; }
    public TimeSpan LinkValidity { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// The primary transport (PLAN §3): Banter frames over a CupriNet Arcanum channel. Pairing is
/// Conjoin (client dials the server's signed mesh-magnet link), authentication is Consecration
/// against a shared watchword, and frames ride Conduits — CupriNet's channel API is
/// message-oriented, so no extra framing layer is needed (the §3 spike question, answered).
/// </summary>
public sealed class CupriNetBanterTransport(CupriNetTransportOptions options) : IBanterClientTransport, IBanterServerTransport, IAsyncDisposable
{
    /// <summary>Conduit protocol id for BanterProtocol envelope frames.</summary>

    private readonly SemaphoreSlim _nodeGate = new(1, 1);
    private CupriNode? _node;
    private Watchword? _watchword;

    private async Task<CupriNode> GetNodeAsync(CancellationToken cancellationToken)
    {
        await _nodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_node is not null)
            {
                return _node;
            }

            Directory.CreateDirectory(options.DataDirectory);
            var suite = new BouncyCastleSuite();
            var masterKey = KeyFileMasterKey.LoadOrCreate(Path.Combine(options.DataDirectory, "master.key"));
            var store = new FileSecretStore(Path.Combine(options.DataDirectory, "secrets"), new AeadDataProtector(suite, masterKey));
            _node = await CupriNode.CreateAsync(new CupriNodeOptions
            {
                Concordium = options.NetworkId,
                ListenAddress = options.ListenAddress,
                ListenPort = options.ListenPort,
                Suite = suite,
                SecretStore = store,
                EnableLanDiscovery = options.EnableLanDiscovery,
                EnablePortMapping = options.EnablePortMapping,
                // Banter hands the link directly to its users (paste/QR), so LAN-only servers
                // must advertise their local address or the link is beaconless (see
                // CupriChatLite's identical opt-in).
                AdvertiseLocalAddresses = true,
            }, cancellationToken).ConfigureAwait(false);

            _watchword = ParseWatchword(options.Watchword);
            return _node;
        }
        finally
        {
            _nodeGate.Release();
        }
    }

    private static Watchword ParseWatchword(string secret)
    {
        // Accept a raw CupriNet watchword ("Name#Salt") verbatim; otherwise derive one from the
        // free-form secret the way CupriChatLite derives channel codes.
        if (Watchword.TryParse(secret, out var literal))
        {
            return literal;
        }

        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("banter/watchword/" + secret)).AsSpan(0, 16).ToArray();
        var code = $"banter#{System.Buffers.Text.Base64Url.EncodeToString(salt)}";
        return Watchword.TryParse(code, out var derived)
            ? derived
            : throw new InvalidOperationException("Failed to derive a CupriNet watchword from the configured secret.");
    }

    // ---- Client side ----

    public async Task<IBanterConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        if (!IntonationUri.TryParse(endpoint.OriginalString.Trim(), out var intonation, out var error))
        {
            throw new ArgumentException($"'{endpoint}' is not a valid cuprinet intonation link: {error}", nameof(endpoint));
        }

        var node = await GetNodeAsync(cancellationToken).ConfigureAwait(false);
        var peer = await node.ConjoinAsync(intonation, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        var session = await node.ConsecrateAsync(peer, _watchword!, DateTimeOffset.UtcNow, new ConsecrateOptions(), cancellationToken)
            .ConfigureAwait(false);
        return new CupriNetConnection(session, peer.PeerSigil.ToString() ?? "cuprinet-peer");
    }

    // ---- Server side ----

    public async Task<IBanterListener> ListenAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        var node = await GetNodeAsync(cancellationToken).ConfigureAwait(false);
        return new CupriNetListener(node, _watchword!, options.LinkValidity);
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
        {
            try
            {
                await _node.DisposeAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already gone.
            }
        }

        _nodeGate.Dispose();
    }

    private sealed class CupriNetListener(CupriNode node, Watchword watchword, TimeSpan linkValidity) : IBanterListener
    {
        public Uri LocalEndpoint => new(node.IntoneUri(linkValidity, DateTimeOffset.UtcNow));

        public async Task<IBanterConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var peer = await node.AcceptAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var session = await node.ConsecrateAsync(peer, watchword, DateTimeOffset.UtcNow, new ConsecrateOptions(), cancellationToken)
                        .ConfigureAwait(false);
                    return new CupriNetConnection(session, peer.PeerSigil.ToString() ?? "cuprinet-peer");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // A peer that paired but failed Consecration (wrong watchword, dropped
                    // mid-handshake) is not a connection; keep accepting.
                }
            }
        }

        /// <summary>The node is owned by the transport (it may also serve outbound connects);
        /// disposing the listener just stops this accept surface. The server's accept loop is
        /// unwound by its own cancellation token.</summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CupriNetConnection(ArcanumSession session, string remoteDescription) : IBanterConnection
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public string RemoteDescription { get; } = remoteDescription;

        public async ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            var conduitFrame = new ConduitFrame
            {
                ProtocolId = BanterConduit.ProtocolId,
                SchemaVersion = 1,
                Flags = 0,
                Payload = frame.ToArray(),
            };
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await session.Conduits.SendAsync(conduitFrame, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                ConduitFrame? frame;
                try
                {
                    frame = await session.Conduits.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Session torn down (peer gone, channel closed) — clean end of stream.
                    return null;
                }

                if (frame is null)
                {
                    return null;
                }

                if (frame.ProtocolId == BanterConduit.ProtocolId)
                {
                    return frame.Payload;
                }

                // Ignore frames from other conduit protocols on a shared session.
            }
        }

        public async ValueTask DisposeAsync()
        {
            _sendLock.Dispose();
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Session may already be gone with the peer.
            }
        }
    }
}
