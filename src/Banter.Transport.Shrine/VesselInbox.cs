using System.Threading.Channels;

namespace Banter.Transport.Shrine;

/// <summary>
/// The inbound half of a vessel: messages arrive from somewhere that cannot be awaited — a browser
/// callback, a native event — and are read back as a stream.
///
/// <para><b>Why this is not just a <see cref="Channel{T}"/>.</b> A vessel ends in two ways that a
/// channel reports as one. The peer leaving is ordinary and reads back as null; the peer sending
/// something impossible is a fault, and if it also reads back as null the stack above sees a clean
/// goodbye and the desync shows up somewhere else entirely, much later. So the reason is held here
/// and raised on the read that reaches it.</para>
///
/// <para>Platform-free on purpose. The browser channel it was extracted from is bound to
/// <c>[JSImport]</c> methods and cannot be constructed off a browser, which is why its receive path
/// had never been tested — the same split as <c>QrScan</c> and the scanner activity.</para>
/// </summary>
public sealed class VesselInbox
{
    private readonly Channel<byte[]> _messages = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private volatile string? _fault;

    /// <summary>Why the vessel ended badly, or null when it ended well or has not ended.</summary>
    public string? Fault => _fault;

    /// <summary>A message arrived. Ignored once the vessel has ended, rather than throwing at a
    /// callback that has nowhere to put an exception.</summary>
    public void Deliver(byte[] message) => _messages.Writer.TryWrite(message);

    /// <summary>The peer left. Ordinary; reads drain what is queued and then answer null.</summary>
    public void Close() => _messages.Writer.TryComplete();

    /// <summary>
    /// The vessel ended badly. Messages already queued are still delivered — they arrived before
    /// the fault and are as good as any other — and the read that reaches the end raises this.
    /// </summary>
    public void Fail(string why)
    {
        // First reason wins. A failure often causes a second one on the way down, and the one that
        // explains the others is the one that happened first.
        _fault ??= why;
        _messages.Writer.TryComplete();
    }

    /// <summary>
    /// The next message, or null once the peer has gone. Latches: having answered null it keeps
    /// answering null, so a receive loop cannot hang on an ended vessel.
    /// </summary>
    /// <exception cref="InvalidOperationException">The vessel ended badly. Raised on every read
    /// past the end, not just the first: a loop that swallowed one and came back would otherwise be
    /// told the ending was clean.</exception>
    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _messages.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            if (_fault is { } why)
            {
                throw new InvalidOperationException(why);
            }

            // Null, not empty: an empty message is one a vessel can legitimately carry.
            return null;
        }
    }
}
