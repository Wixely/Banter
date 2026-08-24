namespace Banter.Protocol.Transport;

/// <summary>
/// One bidirectional frame pipe between two peers. Frames are opaque byte payloads — envelope
/// encoding stays with the caller. Implementations must allow concurrent sends; receives are
/// single-reader.
/// </summary>
public interface IBanterConnection : IAsyncDisposable
{
    /// <summary>Human-readable remote endpoint, for logs.</summary>
    string RemoteDescription { get; }

    ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);

    /// <summary>Receives the next frame, or null when the peer closed cleanly.</summary>
    ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default);
}

/// <summary>Client side of the transport seam (PLAN §3): CupriNet is the primary
/// implementation, TCP the fallback, and everything above this interface cannot tell.</summary>
public interface IBanterClientTransport
{
    Task<IBanterConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default);
}

/// <summary>Server side of the transport seam.</summary>
public interface IBanterServerTransport
{
    Task<IBanterListener> ListenAsync(Uri endpoint, CancellationToken cancellationToken = default);
}

public interface IBanterListener : IAsyncDisposable
{
    /// <summary>The bound endpoint — differs from the requested one when port 0 was asked for.</summary>
    Uri LocalEndpoint { get; }

    /// <summary>Accepts the next incoming connection; throws <see cref="OperationCanceledException"/>
    /// on cancellation and <see cref="ObjectDisposedException"/> after disposal.</summary>
    Task<IBanterConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
