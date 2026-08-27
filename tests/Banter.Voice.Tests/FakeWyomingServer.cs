using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Banter.Voice.Tests;

/// <summary>One event as the fake server saw it, or as it is about to send one.</summary>
internal sealed record WireEvent(string Type, JsonObject Data, byte[] Payload)
{
    public static WireEvent Of(string type, JsonObject? data = null, byte[]? payload = null) =>
        new(type, data ?? [], payload ?? []);

    /// <summary>Set to send this event's data as a length-prefixed block instead of inline.</summary>
    public bool SeparateDataBlock { get; init; }
}

/// <summary>
/// A Wyoming service, for tests.
///
/// <para>Its framing is written independently of the client's on purpose. A test that reused the
/// adapter's own reader and writer would agree with the adapter about a wrong wire format and pass
/// anyway; this one parses the bytes from the protocol description instead.</para>
/// </summary>
internal sealed class FakeWyomingServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<IReadOnlyList<WireEvent>, IEnumerable<WireEvent>> _respond;
    private readonly Task _loop;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<WireEvent> _received = [];

    private FakeWyomingServer(
        TcpListener listener,
        Func<IReadOnlyList<WireEvent>, IEnumerable<WireEvent>> respond)
    {
        _listener = listener;
        _respond = respond;
        _loop = Task.Run(AcceptAsync);
    }

    /// <summary>
    /// Starts on a free port. <paramref name="respond"/> is handed everything the client sent, up
    /// to and including the event that ends its request, and returns the reply.
    /// </summary>
    public static FakeWyomingServer Start(Func<IReadOnlyList<WireEvent>, IEnumerable<WireEvent>> respond)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FakeWyomingServer(listener, respond);
    }

    public string Host => "127.0.0.1";

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyList<WireEvent> Received
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                await using var stream = client.GetStream();

                var request = new List<WireEvent>();
                while (await ReadAsync(stream, _stopping.Token).ConfigureAwait(false) is { } e)
                {
                    request.Add(e);
                    lock (_received)
                    {
                        _received.Add(e);
                    }

                    // Both request shapes end on a known event.
                    if (e.Type is "audio-stop" or "synthesize")
                    {
                        break;
                    }
                }

                foreach (var reply in _respond(request))
                {
                    await WriteAsync(stream, reply, _stopping.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // Shutdown, or a client that hung up. Either way the test is over.
        }
    }

    /// <summary>Reads one event byte by byte — slow, and beyond argument about where a frame ends.</summary>
    private static async Task<WireEvent?> ReadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            line.Add(one[0]);
        }

        var header = JsonNode.Parse(Encoding.UTF8.GetString([.. line])) as JsonObject
            ?? throw new IOException("header was not a JSON object");

        var data = header["data"]?.DeepClone() as JsonObject ?? [];

        if (header["data_length"]?.GetValue<int>() is > 0 and var dataLength)
        {
            var extra = new byte[dataLength];
            await stream.ReadExactlyAsync(extra, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(Encoding.UTF8.GetString(extra)) is JsonObject more)
            {
                foreach (var pair in more)
                {
                    data[pair.Key] = pair.Value?.DeepClone();
                }
            }
        }

        var payload = Array.Empty<byte>();
        if (header["payload_length"]?.GetValue<int>() is > 0 and var payloadLength)
        {
            payload = new byte[payloadLength];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        return new WireEvent(header["type"]!.GetValue<string>(), data, payload);
    }

    private static async Task WriteAsync(NetworkStream stream, WireEvent e, CancellationToken cancellationToken)
    {
        var header = new JsonObject { ["type"] = e.Type };
        byte[] dataBlock = [];

        if (e.Data.Count > 0)
        {
            if (e.SeparateDataBlock)
            {
                dataBlock = Encoding.UTF8.GetBytes(e.Data.ToJsonString());
                header["data_length"] = dataBlock.Length;
            }
            else
            {
                header["data"] = e.Data.DeepClone();
            }
        }

        if (e.Payload.Length > 0)
        {
            header["payload_length"] = e.Payload.Length;
        }

        await stream.WriteAsync(Encoding.UTF8.GetBytes(header.ToJsonString() + "\n"), cancellationToken)
            .ConfigureAwait(false);
        if (dataBlock.Length > 0)
        {
            await stream.WriteAsync(dataBlock, cancellationToken).ConfigureAwait(false);
        }

        if (e.Payload.Length > 0)
        {
            await stream.WriteAsync(e.Payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _stopping.Dispose();
    }
}
