using System.Net;
using CupriNet.Nodestar;
using CupriNet.Vessel;

namespace Banter.Transport.Shrine;

/// <summary>
/// The site's own front door: accepts vessels and serves the node's site on each one.
///
/// <para>A node's listen port is not this. Connecting there reaches the <b>node</b>, which
/// completes a node-to-node handshake and hands back a paired peer with no Shrine behind it — every
/// rite attempted on that session then answers with a closed stream. Serving a site over a vessel
/// is a separate act, and this is it (CupriNodestar#2).</para>
///
/// <para>Only WebRTC routes into the Pilgrimage on its own, because the browser gate accepts the
/// DataChannel itself. Every other transport — TCP here, and an onion circuit later — needs a
/// listener like this one.</para>
/// </summary>
public sealed class ShrineVesselHost : IAsyncDisposable
{
    private readonly NodestarApplication _node;
    private readonly VesselListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _accepting;

    public ShrineVesselHost(NodestarApplication node, IPEndPoint endpoint)
    {
        _node = node;
        _listener = new VesselListener(endpoint);
    }

    /// <summary>Where clients dial. Only meaningful after <see cref="Start"/>.</summary>
    public IPEndPoint LocalEndPoint => _listener.LocalEndPoint;

    public void Start(CancellationToken cancellationToken = default)
    {
        _listener.Start();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, cancellationToken);
        _accepting = AcceptLoopAsync(linked.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IVessel vessel;
            try
            {
                vessel = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Not awaited: a pilgrimage lasts as long as the visitor does, and awaiting it here
            // would serve one client at a time.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _node.AcceptPilgrimageAsync(vessel, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // One visitor's failed handshake is not the listener's business. A caller who
                    // wants these needs a seam for them; nothing does yet.
                }
            }, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_listener is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        if (_accepting is { } accepting)
        {
            try
            {
                await accepting.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: that is how the loop ends.
            }
        }

        _stopping.Dispose();
    }
}
