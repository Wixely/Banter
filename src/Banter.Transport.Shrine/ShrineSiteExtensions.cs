using CupriNet.Nodestar;

namespace Banter.Transport.Shrine;

/// <summary>
/// Where Banter meets Nodestar: the adapters that turn the concrete session types into
/// <see cref="IShrineFrames"/>, and the one call that puts a Banter server on an L2 site.
/// </summary>
public static class ShrineSite
{
    /// <summary>
    /// Banter's conduit protocol id. Chosen freely — the rite keeps no registry — and checked by
    /// Nodestar itself: a frame under any other id ends the session and tells the peer why, rather
    /// than being quietly ignored.
    /// </summary>
    public const uint BanterProtocolId = 0xBA1E70;

    /// <summary>
    /// Serves Banter over this site's conduit, returning the listener to hand to
    /// <c>BanterServer</c>.
    ///
    /// <para>The handler registered here is not awaited: it lives as long as the visitor does.</para>
    /// </summary>
    public static ShrineBanterListener ServeBanter(this SiteBuilder site, Uri endpoint)
    {
        var listener = new ShrineBanterListener(endpoint);

        site.OnSession(BanterProtocolId, (session, cancellationToken) =>
            listener.HandleSessionAsync(
                new SiteSessionFrames(session),
                $"pilgrim:{session.ProtocolId:x}",
                cancellationToken));

        return listener;
    }
}

/// <summary>The server half — a <c>SiteSession</c> from Nodestar's <c>OnSession</c>.</summary>
public sealed class SiteSessionFrames(SiteSession session) : IShrineFrames
{
    public int MaxFrameBytes => session.MaxFrameBytes;

    public string? EndReason => session.EndReason;

    public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default) =>
        session.SendAsync(frame, cancellationToken);

    public Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default) =>
        session.ReceiveAsync(cancellationToken);

    public Task EndAsync(string reason, CancellationToken cancellationToken = default) =>
        session.EndAsync(reason, cancellationToken);
}
