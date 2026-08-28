using Banter.Protocol.Transport;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;

namespace Banter.Transport.Shrine;

/// <summary>The client half — a <c>ConduitSession</c> reached through a Pilgrimage.</summary>
public sealed class ConduitSessionFrames(ConduitSession session, int maxFrameBytes) : IShrineFrames
{
    private string? _endReason;

    public int MaxFrameBytes => maxFrameBytes;

    public string? EndReason => _endReason;

    public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default) =>
        session.SendAsync(
            new ConduitFrame
            {
                ProtocolId = BanterConduit.ProtocolId,
                SchemaVersion = 1,
                Flags = 0,
                Payload = frame.ToArray(),
            },
            cancellationToken);

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ConduitFrame? frame;
            try
            {
                frame = await session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // The peer went rather than closed. Indistinguishable above the transport, and
                // the caller already handles a clean end.
                return null;
            }

            if (frame is null)
            {
                return null;
            }

            if (frame.IsSealed)
            {
                // The site turned us away — a wrong protocol id, or a rule of its own. The reason
                // is worth keeping: it is the difference between "they left" and "we were refused".
                _endReason = frame.SealReason;
                return null;
            }

            if (frame.ProtocolId == BanterConduit.ProtocolId)
            {
                return frame.Payload;
            }

            // Another protocol sharing the session. Not ours, and not an error.
        }
    }

    public Task EndAsync(string reason, CancellationToken cancellationToken = default)
    {
        _endReason = reason;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Dials a Banter server that lives on a CupriNet site (PLAN §2.5): conjoin the node, make a
/// Pilgrimage to the site's Signet, and take the conduit from the resulting
/// <see cref="ShrineSession"/>.
///
/// <para>The vessel comes from the caller, because who owns the node differs by head — a desktop
/// client makes its own, and the browser client is handed one by the page it was served from.</para>
/// </summary>
public sealed class ShrineClientTransport(
    Func<CancellationToken, Task<IVessel>> vessel,
    Sigil siteSignet,
    Concordium network,
    ICryptoSuite suite) : IBanterClientTransport
{
    /// <summary>
    /// A conservative frame bound for the client side. The server reads the real figure from its
    /// own session; a Pilgrim has no equivalent, so this is the rite's own ceiling and the same
    /// number a site would report on the WebRTC path.
    /// </summary>
    public int MaxFrameBytes { get; init; } = 196608;

    public async Task<IBanterConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        var carrier = await vessel(cancellationToken).ConfigureAwait(false);

        var shrine = await Pilgrimage
            .OverVesselAsync(carrier, siteSignet, network, suite, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ShrineClientConnection(
            new ShrineConnection(new ConduitSessionFrames(shrine.Conduits, MaxFrameBytes), siteSignet.ToString() ?? "site"),
            shrine);
    }

    /// <summary>
    /// Keeps the Pilgrimage alive for as long as the connection is, and disposes it after. The
    /// conduit is a rite <i>on</i> that session, so letting it go first would pull the floor out.
    /// </summary>
    private sealed class ShrineClientConnection(ShrineConnection inner, ShrineSession shrine) : IBanterConnection
    {
        public string RemoteDescription => inner.RemoteDescription;

        public ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default) =>
            inner.SendFrameAsync(frame, cancellationToken);

        public ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default) =>
            inner.ReceiveFrameAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await shrine.DisposeAsync().ConfigureAwait(false);
        }
    }
}
