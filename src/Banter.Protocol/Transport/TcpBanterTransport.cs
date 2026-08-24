using System.Net;
using System.Net.Sockets;

namespace Banter.Protocol.Transport;

/// <summary>
/// The plain-TCP fallback transport: <c>tcp://host:port</c>, length-prefixed frames via
/// <see cref="BanterFraming"/>. No encryption — production deployments get that from CupriNet
/// (Noise) or a TLS wrapper; this exists so transport problems never stall the suite (PLAN §3).
/// </summary>
public sealed class TcpBanterTransport : IBanterClientTransport, IBanterServerTransport
{
    public const string Scheme = "tcp";

    public async Task<IBanterConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        ValidateScheme(endpoint);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new TcpConnection(client);
    }

    public Task<IBanterListener> ListenAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        ValidateScheme(endpoint);
        var address = endpoint.Host switch
        {
            "0.0.0.0" or "*" or "+" => IPAddress.Any,
            "localhost" => IPAddress.Loopback,
            var host => IPAddress.Parse(host),
        };
        var listener = new TcpListener(address, endpoint.Port);
        listener.Start();
        return Task.FromResult<IBanterListener>(new TcpBanterListener(listener));
    }

    private static void ValidateScheme(Uri endpoint)
    {
        if (!string.Equals(endpoint.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"TcpBanterTransport handles {Scheme}:// endpoints, not {endpoint.Scheme}://.", nameof(endpoint));
        }
    }

    private sealed class TcpBanterListener(TcpListener listener) : IBanterListener
    {
        public Uri LocalEndpoint
        {
            get
            {
                var bound = (IPEndPoint)listener.LocalEndpoint;
                return new Uri($"{Scheme}://{bound.Address}:{bound.Port}");
            }
        }

        public async Task<IBanterConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            return new TcpConnection(client);
        }

        public ValueTask DisposeAsync()
        {
            listener.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TcpConnection : IBanterConnection
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public TcpConnection(TcpClient client)
        {
            _client = client;
            _client.NoDelay = true;
            _stream = client.GetStream();
            RemoteDescription = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }

        public string RemoteDescription { get; }

        public async ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await BanterFraming.WriteFrameAsync(_stream, frame, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default) =>
            BanterFraming.ReadFrameAsync(_stream, cancellationToken: cancellationToken);

        public ValueTask DisposeAsync()
        {
            _sendLock.Dispose();
            _stream.Dispose();
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
