using Banter.Protocol.Transport;

namespace Banter.Transport.Shrine;

/// <summary>
/// The frame pipe a Shrine conduit offers, narrowed to what Banter needs.
///
/// <para>An interface rather than the concrete <c>SiteSession</c> and <c>ConduitSession</c>
/// because neither can be constructed outside its own assembly — without this seam none of the
/// bridging below could be tested at all, only run.</para>
/// </summary>
public interface IShrineFrames
{
    /// <summary>The largest frame this conduit will carry. Read, never assumed: the number
    /// differs between the WebRTC and Arcanum channel paths.</summary>
    int MaxFrameBytes { get; }

    /// <summary>Why the far end went away, or null when it simply did.</summary>
    string? EndReason { get; }

    Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);

    /// <summary>The next frame, or null once the far end has gone. Null latches.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);

    Task EndAsync(string reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// A Shrine conduit presented as an <see cref="IBanterConnection"/>, so everything above the
/// transport seam — handshake, auth, rooms, history, agents, tools — runs over CupriNet's L2
/// unchanged (PLAN §2.5).
///
/// <para>Two things the conduit gives that no other Banter transport does, and which are why this
/// class is thin: sends are already serialised by <c>ConduitSession</c>'s own lock, so there is no
/// semaphore here as there is in the WebSocket transport; and a clean close latches, so a receive
/// loop cannot be left hanging on a session that has ended.</para>
/// </summary>
public sealed class ShrineConnection(IShrineFrames frames, string remote) : IBanterConnection
{
    public string RemoteDescription => remote;

    /// <summary>What one frame may carry here. Smaller than Banter's own 4 MB ceiling.</summary>
    public int MaxFrameBytes => frames.MaxFrameBytes;

    /// <summary>Why the peer went, once it has. Null while open, and null for an ordinary leave.</summary>
    public string? EndReason => frames.EndReason;

    public async ValueTask SendFrameAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default)
    {
        if (frame.Length > frames.MaxFrameBytes)
        {
            // Refused here rather than at the far end, and named: BanterProtocol declares no bound
            // on a frame, so this is the one place the conduit's limit becomes visible. A history
            // page is the realistic offender — a hundred long agent replies will pass 192 KiB —
            // and bulk belongs in a relic rather than a frame.
            throw new InvalidOperationException(
                $"A {frame.Length}-byte frame exceeds this conduit's {frames.MaxFrameBytes}-byte limit. " +
                "Send fewer messages per page, or carry bulk as a relic.");
        }

        await frames.SendAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default) =>
        await frames.ReceiveAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Ends the session from this side, with a reason the far end can read.
    ///
    /// <para>Best-effort: the conduit may already be gone, and a transport being disposed is not
    /// a place to raise about it.</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await frames.EndAsync("closed", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already ended, or the channel went with the peer.
        }
    }
}
