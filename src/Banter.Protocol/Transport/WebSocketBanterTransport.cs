using System.Net;
using System.Net.WebSockets;

namespace Banter.Protocol.Transport;

/// <summary>
/// The WebSocket transport: <c>ws://host:port/</c>.
///
/// <para><b>The fallback, not the plan's primary browser path.</b> PLAN §2.5 wants the web client
/// on CupriNet's WebRTC DataChannel, which a browser can also use and which keeps the mesh's Noise
/// encryption end to end; this is the safety net §10 names under that risk. It exists because the
/// DataChannel's browser-side story is still an open Phase 0 spike — no WASM client library is
/// named for it — and because a socket is the one thing script definitely cannot open.</para>
///
/// <para>It earns its place away from the browser too: <c>ws://</c> passes through the reverse
/// proxies and firewalls that plain <c>tcp://</c> does not, and <c>wss://</c> behind nginx is how a
/// deployment gets TLS without CupriNet.</para>
///
/// <para><b>No length prefix.</b> WebSocket is already message-framed, so one Banter frame is one
/// binary message and <see cref="BanterFraming"/> is not involved. A message can still arrive in
/// several reads, which is what the receive loop below is for.</para>
/// </summary>
public sealed class WebSocketBanterTransport : IBanterClientTransport, IBanterServerTransport
{
    public const string Scheme = "ws";

    public const string SecureScheme = "wss";

    /// <summary>
    /// Refusal point for one frame. Without a cap a peer can make the server allocate without
    /// bound simply by never setting the end-of-message flag.
    /// </summary>
    public int MaxFrameBytes { get; init; } = 16 * 1024 * 1024;

    public async Task<IBanterConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        ValidateScheme(endpoint);

        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new WebSocketConnection(socket, endpoint.ToString(), MaxFrameBytes);
    }

    public Task<IBanterListener> ListenAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        ValidateScheme(endpoint);

        var listener = new HttpListener();

        // HttpListener speaks http, not ws: the upgrade happens on an ordinary request. "+" binds
        // every interface, which is what 0.0.0.0 and * mean to the other transports.
        var host = endpoint.Host switch
        {
            "0.0.0.0" or "*" or "localhost" => endpoint.Host == "localhost" ? "localhost" : "+",
            var other => other,
        };

        listener.Prefixes.Add($"http://{host}:{endpoint.Port}/");
        listener.Start();
        return Task.FromResult<IBanterListener>(new WebSocketListener(listener, endpoint, MaxFrameBytes));
    }

    private static void ValidateScheme(Uri endpoint)
    {
        if (!string.Equals(endpoint.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, SecureScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"WebSocketBanterTransport handles {Scheme}:// endpoints, not {endpoint.Scheme}://.",
                nameof(endpoint));
        }
    }

    private sealed class WebSocketListener(HttpListener listener, Uri requested, int maxFrameBytes) : IBanterListener
    {
        public Uri LocalEndpoint => requested;

        public async Task<IBanterConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (HttpListenerException) when (!listener.IsListening)
                {
                    throw new ObjectDisposedException(nameof(WebSocketListener));
                }

                if (!context.Request.IsWebSocketRequest)
                {
                    // Anything that is not an upgrade is refused and the loop continues, so a
                    // health probe or a stray browser cannot occupy the accept. This is also where
                    // serving the WASM client from the same port would go (PLAN §2.5).
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    context.Response.Close();
                    continue;
                }

                var accepted = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
                return new WebSocketConnection(
                    accepted.WebSocket,
                    context.Request.RemoteEndPoint?.ToString() ?? "websocket",
                    maxFrameBytes);
            }
        }

        public ValueTask DisposeAsync()
        {
            listener.Close();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WebSocketConnection(WebSocket socket, string remote, int maxFrameBytes) : IBanterConnection
    {
        // The interface requires concurrent sends; WebSocket.SendAsync does not allow them, and
        // two overlapping sends interleave into one corrupt message rather than failing loudly.
        private readonly SemaphoreSlim _sending = new(1, 1);
        private readonly byte[] _scratch = new byte[16 * 1024];

        public string RemoteDescription => remote;

        public async ValueTask SendFrameAsync(
            ReadOnlyMemory<byte> frame,
            CancellationToken cancellationToken = default)
        {
            await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket
                    .SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sending.Release();
            }
        }

        public async ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default)
        {
            using var frame = new MemoryStream();

            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(_scratch, cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // A peer that vanished rather than closing. Indistinguishable from a close to
                    // everything above this, and the caller handles one already.
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (frame.Length + result.Count > maxFrameBytes)
                {
                    throw new InvalidOperationException(
                        $"A frame from {remote} passed {maxFrameBytes} bytes without ending.");
                }

                frame.Write(_scratch.AsSpan(0, result.Count));

                if (result.EndOfMessage)
                {
                    return frame.ToArray();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    // CloseOutputAsync, not CloseAsync. CloseAsync sends the close frame and then
                    // waits for the peer's reply — and when both ends dispose at once, as they do
                    // whenever a connection is torn down from one place, each waits for a reply the
                    // other will never send because it is doing the same thing. The peer still sees
                    // a clean close either way: its next receive returns a Close message.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket
                        .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Closing politely is best-effort; the socket is going away regardless.
            }

            socket.Dispose();
            _sending.Dispose();
        }
    }
}
