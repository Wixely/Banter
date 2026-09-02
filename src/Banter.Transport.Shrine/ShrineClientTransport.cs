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
/// Dials a Banter server that lives on a CupriNet site (PLAN §2.5).
///
/// <para>The endpoint is the node's <b>intonation link</b>, not a host and port: a signed link
/// already carries the site's Sigil and the network it belongs to, so a client needs nothing else
/// to know what it is talking to and to verify that it reached it. That is also what makes the URI
/// on this seam meaningful rather than decorative.</para>
///
/// <para>How to <i>reach</i> the node is separate from how to <i>identify</i> it, so the vessel is
/// the caller's to open: a desktop client dials a beacon over TCP, a browser arrives over WebRTC,
/// and an onion client over a circuit. No node is constructed on this side — a Pilgrim needs only
/// a vessel, which is precisely what lets a browser be one.</para>
/// </summary>
public sealed class ShrineClientTransport(
    Func<Intonation, CancellationToken, Task<IVessel>> dial,
    ICryptoSuite suite) : IBanterClientTransport
{
    public async Task<IBanterConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        if (!IntonationUri.TryParse(endpoint.OriginalString.Trim(), out var intonation, out _))
        {
            throw new ArgumentException(
                $"'{endpoint}' is not a CupriNet intonation link.", nameof(endpoint));
        }

        if (intonation.Shrine is not { } shrine)
        {
            // A link from a node that hosts no site, or one issued before the site existed. Worth
            // saying plainly: the Pilgrimage would otherwise fail with nothing to point at.
            throw new ArgumentException(
                $"The link for '{intonation.Moniker}' advertises no site to make a pilgrimage to.",
                nameof(endpoint));
        }

        var vessel = await dial(intonation, cancellationToken).ConfigureAwait(false);

        // The SITE's Signet, not the node's InviterSigil. Pinning the node succeeds — into a
        // session with no Shrine behind it, where every rite answers with a closed stream
        // (CupriNodestar#2). The refusal when this does not match is the pin doing its job.
        var shrineSession = await Pilgrimage
            .OverVesselAsync(vessel, shrine, intonation.Network, suite, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ShrineClientConnection(
            new ShrineConnection(
                // Read, not assumed: the ceiling differs on the Arcanum channel path.
                new ConduitSessionFrames(shrineSession.Conduits, shrineSession.Conduits.MaxPayloadBytes),
                intonation.ShrineAddress ?? shrine.ToString() ?? "site"),
            shrineSession);
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

            // No catch here. A session the far side had already ended used to throw when disposed
            // again rather than doing nothing (CupriNodestar#3), and closing a connection must not
            // fail — but swallowing ObjectDisposedException made "already closed, nothing to do"
            // indistinguishable from a genuine disposal fault anywhere beneath it. CupriNet 0.6.0
            // made dispose idempotent; ClosingAConnectionTwiceIsNotAnError is what says so here.
            await shrine.DisposeAsync().ConfigureAwait(false);
        }
    }
}
