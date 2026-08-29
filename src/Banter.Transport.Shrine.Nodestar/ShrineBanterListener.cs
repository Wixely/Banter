using System.Threading.Channels;
using Banter.Protocol.Transport;

namespace Banter.Transport.Shrine;

/// <summary>
/// Turns Nodestar's push model into the pull model <see cref="IBanterListener"/> expects.
///
/// <para><b>The inversion is the whole point of this class.</b> Nodestar calls a handler once per
/// visitor and <i>ends the session when that handler returns</i>; Banter's server asks a listener
/// for the next connection and owns it from there. So each handler hands its session to
/// <see cref="AcceptAsync"/> through a queue and then parks — the park is not idling, it is what
/// holds the session open — until the room engine disposes the connection.</para>
///
/// <para>Get that wrong and the failure is quiet: return from the handler and the visitor is
/// disconnected the instant they arrive, with the server still holding a connection it thinks is
/// live.</para>
/// </summary>
public sealed class ShrineBanterListener(Uri endpoint) : IBanterListener
{
    private readonly Channel<Arrival> _arrivals = Channel.CreateUnbounded<Arrival>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    private readonly CancellationTokenSource _stopping = new();

    /// <summary>A session waiting to be accepted, and the latch that keeps its handler parked.</summary>
    private sealed record Arrival(IShrineFrames Frames, string Remote, TaskCompletionSource Finished);

    /// <summary>Where the node is reachable, for logs. A conduit has no port of its own.</summary>
    public Uri LocalEndpoint => endpoint;

    /// <summary>
    /// The handler to hand to <c>SiteBuilder.OnSession</c>. It runs for as long as the visitor is
    /// connected, so it must not be awaited by the caller that registers it.
    /// </summary>
    public async Task HandleSessionAsync(IShrineFrames frames, string remote, CancellationToken cancellationToken)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_arrivals.Writer.TryWrite(new Arrival(frames, remote, finished)))
        {
            return;                                         // listener disposed; let the session end
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        using var registration = linked.Token.Register(() => finished.TrySetResult());

        // Parked deliberately. Returning from here ends the session under the connection that was
        // just handed out, so this waits for that connection to be disposed.
        await finished.Task.ConfigureAwait(false);
    }

    public async Task<IBanterConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);

        Arrival arrival;
        try
        {
            arrival = await _arrivals.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ShrineBanterListener));
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(ShrineBanterListener));
        }

        return new ParkedConnection(new ShrineConnection(arrival.Frames, arrival.Remote), arrival.Finished);
    }

    /// <summary>
    /// The connection handed to the server, which releases its handler when disposed. A wrapper
    /// rather than a flag on <see cref="ShrineConnection"/> so that class stays about frames and
    /// knows nothing about how it was accepted.
    /// </summary>
    private sealed class ParkedConnection(ShrineConnection inner, TaskCompletionSource finished) : IBanterConnection
    {
        public string RemoteDescription => inner.RemoteDescription;

        public ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default) =>
            inner.SendFrameAsync(frame, cancellationToken);

        public ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default) =>
            inner.ReceiveFrameAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);

            // Only now may the handler return, which is what actually ends the session.
            finished.TrySetResult();
        }
    }

    private int _disposed;

    public ValueTask DisposeAsync()
    {
        // Idempotent on purpose: BanterServer disposes the listener it was given, and whoever built
        // it disposes it too. Both are right to, and the second must not throw.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        _arrivals.Writer.TryComplete();
        _stopping.Cancel();

        // Release every handler still parked, including any queued and never accepted — otherwise
        // the node keeps their sessions open for a listener that has gone.
        while (_arrivals.Reader.TryRead(out var pending))
        {
            pending.Finished.TrySetResult();
        }

        _stopping.Dispose();
        return ValueTask.CompletedTask;
    }
}
