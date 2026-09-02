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
/// is the same code the desktop runs, and everything below the app — the frame loop, input, ARIA —
/// belongs to the host package.
/// </summary>
public sealed partial class BrowserDataChannel : IDataChannel
{
    /// <summary>
    /// How long to wait for the channel to open. On a loopback node this is instant and over a
    /// network it is seconds; anything longer means the node is not answering, and a person
    /// watching a button say "Connecting" forever learns nothing.
    /// </summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The JS module is loaded once, on first use. <see cref="JSHost.ImportAsync"/> rather than a
    /// script tag in the page: the page belongs to the host package now, and a transport that
    /// needed an edit to someone else's HTML would not be one anybody could drop in.
    /// </summary>
    private static Task? _module;

    /// <summary>
    /// One connection per page, because the JS side keeps one peer connection. Static because the
    /// message callback has nowhere to put an instance.
    /// </summary>
    private static BrowserDataChannel? _current;

    /// <summary>
    /// Where a message is copied out of JS. Sized to the largest a DataChannel will carry (the
    /// node negotiates 256 KiB), so a frame at the conduit's ceiling still fits with room over.
    /// Reused: this is on the receive path of every message.
    /// </summary>
    private static readonly byte[] Scratch = new byte[262144];

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
    /// What SCTP negotiated as the largest single message, or 0 before the association is up.
    ///
    /// <para>CupriNet 0.6.0 refuses a vessel that cannot carry a legal rite frame
    /// (<c>RiteTransport.RequiredMessageBytes</c>), naming both numbers, rather than letting the
    /// pairing fail later and namelessly on the wire. That check is only as good as this answer,
    /// so this asks the browser instead of assuming: the negotiated figure is the smaller of what
    /// the two ends offered, and a guess that came out low would refuse connections that work.
    /// Chromium settles on 262144 against this node.</para>
    ///
    /// <para>0 means "not known yet", which the check treats as unbounded and skips. That is the
    /// honest answer before the channel opens and the right one after a failure — the alternative
    /// is refusing a connection on the strength of a number we never had.</para>
    /// </summary>
    public int MaxMessageBytes => ReferenceEquals(_current, this) ? RtcMaxMessageSize() : 0;

    /// <summary>Dials the node the link describes and waits for the channel to open.</summary>
    /// <exception cref="InvalidOperationException">
    /// The link carries no WebRTC endpoint, or the connection failed. A link without one belongs to
    /// a node a browser simply cannot reach, which is worth saying rather than timing out.
    /// </exception>
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

        _module ??= JSHost.ImportAsync("banter/rtc", "../banter-rtc.js");
        await _module.ConfigureAwait(false);

        var channel = new BrowserDataChannel(new DnsEndPoint(host, webRtc.Port));
        _current = channel;

        RtcConnect(
            host,
            webRtc.Port,
            webRtc.IceUfrag,
            webRtc.IcePassword,
            webRtc.FingerprintAlgorithm,
            Convert.ToHexString(webRtc.Fingerprint),
            Drain,
            NotifyClosed);

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
    /// A message is waiting. JS signals rather than hands it over: a callback cannot carry a
    /// <c>byte[]</c> across the boundary at all, and the array marshallings that exist copy element
    /// by element. So the buffer is ours, and JS fills it.
    /// </summary>
    private static void Drain()
    {
        if (_current is not { } channel)
        {
            return;
        }

        while (true)
        {
            var length = RtcReceive(Scratch);
            if (length < 0)
            {
                if (length == -2)
                {
                    // Larger than any DataChannel message the node can send, so something is wrong
                    // with the peer rather than with the buffer. Dropping it silently would show up
                    // much later as a protocol desync.
                    channel._inbound.Writer.TryComplete(
                        new InvalidOperationException(
                            $"A WebRTC message exceeded the {Scratch.Length}-byte receive buffer."));
                }

                return;
            }

            channel._inbound.Writer.TryWrite(Scratch[..length]);
        }
    }

    private static void NotifyClosed() => _current?._inbound.Writer.TryComplete();

    [JSImport("connect", "banter/rtc")]
    internal static partial void RtcConnect(
        string host,
        int port,
        string ufrag,
        string password,
        string fingerprintAlgorithm,
        string fingerprintHex,
        [JSMarshalAs<JSType.Function>] Action onMessage,
        [JSMarshalAs<JSType.Function>] Action onClosed);

    /// <summary>
    /// Copies the next queued message into <paramref name="buffer"/>, returning its length, -1 when
    /// nothing is waiting, or -2 when it would not fit. The view is safe to pass because C# owns
    /// the array — the reverse, a view JS made, fails an assertion inside the marshaller.
    /// </summary>
    [JSImport("receive", "banter/rtc")]
    internal static partial int RtcReceive([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> buffer);

    /// <summary>0 connecting, 1 open, 2 failed, 3 closed.</summary>
    [JSImport("state", "banter/rtc")]
    internal static partial int RtcState();

    [JSImport("error", "banter/rtc")]
    internal static partial string RtcError();

    /// <summary>What SCTP agreed a message may be, or 0 before the association is up.</summary>
    [JSImport("maxMessageSize", "banter/rtc")]
    internal static partial int RtcMaxMessageSize();

    [JSImport("send", "banter/rtc")]
    internal static partial bool RtcSend([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> message);

    [JSImport("close", "banter/rtc")]
    internal static partial void RtcClose();
}
