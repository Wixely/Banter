using System.Net;
using System.Threading.Channels;
using CupriNet.Vessel;

namespace Banter.Transport.Shrine.Tests;

/// <summary>
/// A pair of <see cref="IDataChannel"/> ends joined in memory, with the semantics a browser's
/// reliable-ordered <c>RTCDataChannel</c> has: <b>message-oriented</b>, so boundaries are preserved
/// and never coalesced.
///
/// <para>That is the whole reason this exists rather than another TCP vessel. A stream vessel lets
/// framing bugs hide — a reader that assumes it can keep pulling until it has enough bytes works by
/// accident when the transport happens to coalesce. Here it cannot, so the browser path's framing
/// is exercised on a machine with no browser on it.</para>
/// </summary>
public sealed class LoopbackDataChannel : IDataChannel
{
    private readonly Channel<byte[]> _inbound;
    private Channel<byte[]> _outbound = null!;

    private LoopbackDataChannel(EndPoint local, EndPoint remote)
    {
        LocalEndPoint = local;
        RemoteEndPoint = remote;
        _inbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
    }

    public EndPoint LocalEndPoint { get; }

    public EndPoint RemoteEndPoint { get; }

    /// <summary>How many discrete messages this end has sent, and the largest of them.</summary>
    public int MessagesSent { get; private set; }

    public int LargestMessageSent { get; private set; }

    public static (LoopbackDataChannel Client, LoopbackDataChannel Site) Pair()
    {
        var clientEnd = new IPEndPoint(IPAddress.Loopback, 1);
        var siteEnd = new IPEndPoint(IPAddress.Loopback, 2);

        var client = new LoopbackDataChannel(clientEnd, siteEnd);
        var site = new LoopbackDataChannel(siteEnd, clientEnd);

        client._outbound = site._inbound;
        site._outbound = client._inbound;

        return (client, site);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        // Copied, because the caller owns the buffer it handed us and a real channel would have
        // serialised it away by now.
        MessagesSent++;
        LargestMessageSent = Math.Max(LargestMessageSent, message.Length);
        _outbound.Writer.TryWrite(message.ToArray());
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
            // Null, not empty: the interface spells a closed peer that way, and an empty message is
            // a message a DataChannel can legitimately carry.
            return null;
        }
    }

    /// <summary>Drops the far end, the way a closed <c>RTCDataChannel</c> does.</summary>
    public ValueTask DisposeAsync()
    {
        _outbound.Writer.TryComplete();
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
