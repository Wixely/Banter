using System.Net;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Channels;
using CupriNet.Core;
using CupriNet.Vessel;

namespace Banter.App.Web;

/// <summary>
/// A browser <c>RTCDataChannel</c> presented as an <see cref="IDataChannel"/>, so
/// <c>DataChannelVessel</c> can carry a Pilgrimage over it and Banter's whole stack rides on top
/// unchanged. This is the entirety of what the web head adds to the network: everything above it
/// is the same code the desktop runs.
///
/// <para><b>There is no signalling server, and none is needed.</b> The node is ICE-lite and DTLS
/// passive, and its intonation link already carries the ICE credentials and DTLS fingerprint it
/// would have put in an answer. So the browser makes an offer and then <i>writes the node's answer
/// itself</i> from the link. The link is signed, which is what makes that safe: a forged answer
/// would have to forge the link.</para>
///
/// <para>The SDP shape is not ours to invent — it has to match what the node's DCEP responder
/// expects, down to the channel label and <c>a=setup:passive</c>. It follows Nodestar's own
/// reference client.</para>
/// </summary>
public sealed partial class BrowserDataChannel : IDataChannel
{
    /// <summary>
    /// One connection per page, because the JS side keeps one peer connection. Static because the
    /// message callback comes from JS, which has nowhere to put an instance.
    /// </summary>
    private static BrowserDataChannel? _current;

    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private BrowserDataChannel(EndPoint remote)
    {
        RemoteEndPoint = remote;
        LocalEndPoint = new IPEndPoint(IPAddress.Any, 0);
    }

    public EndPoint RemoteEndPoint { get; }

    public EndPoint LocalEndPoint { get; }

    /// <summary>
    /// Dials the node the link describes and waits for the channel to open.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The link carries no WebRTC endpoint, or the connection failed. A link without one belongs to
    /// a node that a browser simply cannot reach, which is worth saying rather than timing out.
    /// </exception>
    /// <summary>
    /// How long to wait for the channel to open. On a loopback node this is instant and over a
    /// network it is seconds; anything longer means the node is not answering, and a person
    /// watching a button say "Connecting" forever learns nothing.
    /// </summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    public static async Task<BrowserDataChannel> ConnectAsync(
        Intonation intonation,
        CancellationToken cancellationToken = default)
    {
        if (intonation.WebRtc is not { } webRtc || webRtc.Port == 0)
        {
            throw new InvalidOperationException(
                $"The link for '{intonation.Moniker}' advertises no WebRTC endpoint, so a browser " +
                "cannot reach it. The node needs WebRTC enabled.");
        }

        // The link carries the node's reachable addresses but not which of them the WebRTC endpoint
        // listens on — the port is separate, and the host is whichever address is reachable. A
        // plain host beacon is the one a browser can dial.
        var host = intonation.Beacons.FirstOrDefault(b => b.Kind == EndpointKind.Host)?.Host
            ?? intonation.Beacons.FirstOrDefault()?.Host
            ?? throw new InvalidOperationException(
                $"The link for '{intonation.Moniker}' names no address to dial.");

        var channel = new BrowserDataChannel(
            new DnsEndPoint(host, webRtc.Port));
        _current = channel;

        RtcConnect(
            host,
            webRtc.Port,
            webRtc.IceUfrag,
            webRtc.IcePassword,
            webRtc.FingerprintAlgorithm,
            Convert.ToHexString(webRtc.Fingerprint));

        // Polling, because the browser reports readiness through state rather than a promise we can
        // await across the JS boundary. The runtime is single-threaded, so this yields to the event
        // loop — a blocking wait here would stop the very callbacks it is waiting for.
        var deadline = DateTimeOffset.UtcNow + OpenTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (RtcState())
            {
                case 1:
                    return channel;
                case 2:
                case 3:
                    _current = null;
                    RtcClose();
                    throw new InvalidOperationException($"WebRTC: {RtcError()}");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                // ICE never completes against a node that is no longer there — no refusal comes
                // back, because there is nothing to refuse. Naming the address is the useful part:
                // it is nearly always a link outliving the node that issued it.
                _current = null;
                RtcClose();
                throw new TimeoutException(
                    $"No answer from {host}:{webRtc.Port} after {OpenTimeout.TotalSeconds:0}s. " +
                    "The link may be older than the node it names.");
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (!RtcSend(message.ToArray()))
        {
            // The channel went while we were writing. Indistinguishable from the peer leaving, and
            // the reader is about to report exactly that.
            _inbound.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Null, not empty: an empty message is one a DataChannel can legitimately carry.
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();

        if (ReferenceEquals(_current, this))
        {
            _current = null;
            RtcClose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A message arrived on the channel. Reached from JS through <c>Interop</c>, because the
    /// runtime groups exports by declaring type and the host's exports all live on that one.
    /// </summary>
    internal static void Deliver(byte[] message) => _current?._inbound.Writer.TryWrite(message);

    /// <summary>The channel closed.</summary>
    internal static void NotifyClosed() => _current?._inbound.Writer.TryComplete();

    [JSImport("rtcConnect", "banter")]
    internal static partial void RtcConnect(
        string host, int port, string ufrag, string password, string fingerprintAlgorithm, string fingerprintHex);

    /// <summary>0 connecting, 1 open, 2 failed, 3 closed.</summary>
    [JSImport("rtcState", "banter")]
    internal static partial int RtcState();

    [JSImport("rtcError", "banter")]
    internal static partial string RtcError();

    [JSImport("rtcSend", "banter")]
    internal static partial bool RtcSend([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> message);

    [JSImport("rtcClose", "banter")]
    internal static partial void RtcClose();
}
