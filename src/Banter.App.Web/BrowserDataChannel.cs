using System.Net;
using System.Runtime.InteropServices;
using Banter.Transport.Shrine;
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

    /// <summary>
    /// Where messages wait to be read. Shared with the other vessels rather than a channel held
    /// here, because the distinction it keeps — the peer leaving versus the peer sending something
    /// impossible — is the one this class used to lose, and it is tested there.
    /// </summary>
    private readonly VesselInbox _inbound = new();

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
        // listens on — the port is separate, and the host is whichever address is reachable.
        //
        // Which address that is, is MeshDial's question and not one to answer twice. This used to
        // take the Host beacon first and then fall back to whichever beacon came first, which is
        // precisely the ordering MeshDial exists to correct: a node behind a NAT advertises a Host
        // beacon that is valid and unreachable, and "whichever came first" will happily hand back a
        // Relay or an .onion, which a browser dials as a hostname and then waits out.
        var host = MeshDial.HostOrThrow(intonation.Beacons, intonation.Moniker ?? "the server");

        // No module to load: the JS half is linked into the wasm module rather than fetched, so
        // there is nothing to await before calling it.
        var channel = new BrowserDataChannel(new DnsEndPoint(host, webRtc.Port));
        _current = channel;

        Connect(
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
                    throw new InvalidOperationException($"WebRTC: {LastError()}");
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
        if (!Send(message.Span))
        {
            // The channel went while we were writing. Indistinguishable from the peer leaving, and
            // the reader is about to report exactly that.
            _inbound.Close();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _inbound.ReceiveAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        _inbound.Close();

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
            var length = Receive(Scratch);
            if (length < 0)
            {
                if (length == -2)
                {
                    // Larger than any DataChannel message the node can send, so something is wrong
                    // with the peer rather than with the buffer. Dropping it silently would show up
                    // much later as a protocol desync — and completing the channel with the reason
                    // attached, which is what this did, dropped it just as silently: the reader
                    // caught the resulting ChannelClosedException and answered null, so the stack
                    // above read a fault as a goodbye. VesselInbox is where that distinction now
                    // lives, and where it is tested.
                    channel._inbound.Fail(
                        $"A WebRTC message exceeded the {Scratch.Length}-byte receive buffer.");
                }

                return;
            }

            channel._inbound.Deliver(Scratch[..length]);
        }
    }

    private static void NotifyClosed() => _current?._inbound.Close();

    // ---- the boundary -----------------------------------------------------------------------
    //
    // There is no Mono here and therefore no [JSImport]: this host is NativeAOT-LLVM, where the
    // only thing crossing is the C ABI. Calls out are DllImports the Emscripten linker binds to
    // interop/banter-rtc.js (--js-library, see the csproj); calls in are [UnmanagedCallersOnly]
    // exports the JS side reaches as Module._Name.
    //
    // Everything is a number. Strings go out as pointers to their UTF-16 data, which works because
    // a managed string is null-terminated in memory; strings come BACK by being copied into a
    // buffer this side owns, because nothing can be returned by reference. Byte buffers are a
    // pointer and a length, pinned for the duration of the call and no longer - the wasm heap can
    // grow, and a pointer kept across a yield would be stale.

    private const string Rtc = "banterrtc";

    private static unsafe void Connect(
        string host, int port, string ufrag, string password, string algorithm, string fingerprint)
    {
        fixed (char* h = host)
        fixed (char* u = ufrag)
        fixed (char* p = password)
        fixed (char* a = algorithm)
        fixed (char* f = fingerprint)
        {
            RtcConnect(h, port, u, p, a, f);
        }
    }

    private static unsafe int Receive(byte[] buffer)
    {
        fixed (byte* b = buffer)
        {
            return RtcReceive(b, buffer.Length);
        }
    }

    private static unsafe bool Send(ReadOnlySpan<byte> message)
    {
        fixed (byte* m = message)
        {
            return RtcSend(m, message.Length) != 0;
        }
    }

    /// <summary>The last failure the JS side recorded, copied into a buffer this side owns.</summary>
    private static unsafe string LastError()
    {
        // Long enough for a browser's own wording; a longer one is clipped rather than refused,
        // because the reason a connection failed is not worth failing a second time over.
        var buffer = new char[512];
        fixed (char* b = buffer)
        {
            var length = RtcError(b, buffer.Length);
            return length <= 0 ? "unknown" : new string(buffer, 0, length);
        }
    }

    /// <summary>
    /// A message is waiting. Called from JS, so nothing may escape: an exception crossing an
    /// UnmanagedCallersOnly boundary does not unwind into a catch anywhere, it ends the process.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "BanterRtcMessage")]
    internal static void MessageArrived()
    {
        try { Drain(); }
        catch (Exception ex) { Console.Error.WriteLine($"[banter/rtc] draining failed: {ex}"); }
    }

    /// <summary>The channel closed. Same rule as above about escaping.</summary>
    [UnmanagedCallersOnly(EntryPoint = "BanterRtcClosed")]
    internal static void ChannelClosed()
    {
        try { NotifyClosed(); }
        catch (Exception ex) { Console.Error.WriteLine($"[banter/rtc] close failed: {ex}"); }
    }

    [DllImport(Rtc, EntryPoint = "banter_rtc_connect")]
    private static extern unsafe void RtcConnect(
        char* host, int port, char* ufrag, char* password, char* algorithm, char* fingerprint);

    /// <summary>0 connecting, 1 open, 2 failed, 3 closed.</summary>
    [DllImport(Rtc, EntryPoint = "banter_rtc_state")]
    private static extern int RtcState();

    [DllImport(Rtc, EntryPoint = "banter_rtc_error")]
    private static extern unsafe int RtcError(char* buffer, int capacityInChars);

    /// <summary>What SCTP agreed a message may be, or 0 before the association is up.</summary>
    [DllImport(Rtc, EntryPoint = "banter_rtc_max_message_size")]
    private static extern int RtcMaxMessageSize();

    /// <summary>
    /// Copies the next queued message into <paramref name="buffer"/>, returning its length, -1 when
    /// nothing is waiting, or -2 when it would not fit.
    /// </summary>
    [DllImport(Rtc, EntryPoint = "banter_rtc_receive")]
    private static extern unsafe int RtcReceive(byte* buffer, int capacity);

    [DllImport(Rtc, EntryPoint = "banter_rtc_send")]
    private static extern unsafe int RtcSend(byte* message, int length);

    [DllImport(Rtc, EntryPoint = "banter_rtc_close")]
    private static extern void RtcClose();
}
