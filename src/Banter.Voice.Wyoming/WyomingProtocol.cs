using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Banter.Voice.Wyoming;

/// <summary>One Wyoming event: a type, a JSON data object, and an optional binary payload.</summary>
internal sealed record WyomingEvent(string Type, JsonObject Data, ReadOnlyMemory<byte> Payload)
{
    public static WyomingEvent Of(string type, JsonObject? data = null) =>
        new(type, data ?? [], ReadOnlyMemory<byte>.Empty);

    public string? Text => Data["text"]?.GetValue<string>();

    public int? Int(string name) => Data[name] is { } n && n.GetValueKind() == JsonValueKind.Number
        ? n.GetValue<int>()
        : null;
}

/// <summary>
/// A connection to a Wyoming service (PLAN §6): newline-delimited JSON events over TCP, each
/// optionally followed by raw PCM. No dependency and barely any code, which is the reason the plan
/// picked it as the third backend.
///
/// <para>One connection per request. Wyoming services are request-shaped — transcribe this, say
/// that — and a pooled connection would have to track which reply belonged to which caller for no
/// benefit over a socket that costs a millisecond.</para>
/// </summary>
internal sealed class WyomingConnection : IAsyncDisposable
{
    /// <summary>
    /// Refusal point for a header line. A Wyoming header is a short JSON object; anything past
    /// this is a service speaking a different protocol, and reading to a newline that never comes
    /// would otherwise buffer until the process died.
    /// </summary>
    private const int MaxHeaderBytes = 64 * 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    // One buffer for both halves of the read. A header line and the payload after it arrive in the
    // same packets, so anything read past the newline has to be kept — a fresh reader for the
    // payload would start after bytes that were already consumed and silently lose them.
    private byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    private WyomingConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<WyomingConnection> ConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        try
        {
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"No Wyoming service answered at {host}:{port} within {timeout}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new WyomingConnection(client);
    }

    public async Task SendAsync(WyomingEvent e, CancellationToken cancellationToken)
    {
        var header = new JsonObject { ["type"] = e.Type };
        if (e.Data.Count > 0)
        {
            // Inline rather than a length-prefixed block. The protocol allows both, and a reader
            // merges the block into this same object, so inline is the same message with less
            // framing to get wrong.
            header["data"] = e.Data.DeepClone();
        }

        if (e.Payload.Length > 0)
        {
            header["payload_length"] = e.Payload.Length;
        }

        var line = Encoding.UTF8.GetBytes(header.ToJsonString() + "\n");
        await _stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);

        if (e.Payload.Length > 0)
        {
            await _stream.WriteAsync(e.Payload, cancellationToken).ConfigureAwait(false);
        }

        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the next event, or null when the service closed the connection.</summary>
    public async Task<WyomingEvent?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            return null;
        }

        JsonObject header;
        try
        {
            header = JsonNode.Parse(line) as JsonObject
                ?? throw new IOException($"Wyoming header was not an object: {Truncate(line)}");
        }
        catch (JsonException ex)
        {
            throw new IOException($"Unreadable Wyoming header: {Truncate(line)}", ex);
        }

        var type = header["type"]?.GetValue<string>()
            ?? throw new IOException($"Wyoming event had no type: {Truncate(line)}");

        var data = header["data"]?.DeepClone() as JsonObject ?? [];

        // A separate data block is *merged into* the inline object rather than replacing it, which
        // is what the protocol specifies and what the reference implementation does.
        if (header["data_length"]?.GetValue<int>() is > 0 and var dataLength)
        {
            var extra = new byte[dataLength];
            await ReadExactAsync(extra, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(Encoding.UTF8.GetString(extra)) is JsonObject more)
            {
                foreach (var pair in more)
                {
                    data[pair.Key] = pair.Value?.DeepClone();
                }
            }
        }

        var payload = ReadOnlyMemory<byte>.Empty;
        if (header["payload_length"]?.GetValue<int>() is > 0 and var payloadLength)
        {
            var bytes = new byte[payloadLength];
            await ReadExactAsync(bytes, cancellationToken).ConfigureAwait(false);
            payload = bytes;
        }

        return new WyomingEvent(type, data, payload);
    }

    /// <summary>Reads up to the next newline, refilling the shared buffer as needed.</summary>
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var line = Encoding.UTF8.GetString(_buffer, _start, newline - _start);
                _start = newline + 1;
                return line;
            }

            if (_end - _start > MaxHeaderBytes)
            {
                throw new IOException("No Wyoming header line arrived; is this a Wyoming service?");
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;                                // closed cleanly between events
            }
        }
    }

    private async Task ReadExactAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new EndOfStreamException(
                    $"Wyoming service closed with {destination.Length} bytes of the message still to come.");
            }

            var take = Math.Min(destination.Length, _end - _start);
            _buffer.AsMemory(_start, take).CopyTo(destination);
            _start += take;
            destination = destination[take..];
        }
    }

    /// <summary>Reads more from the socket. False at end of stream.</summary>
    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        Compact();

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return false;
        }

        _end += read;
        return true;
    }

    private void Compact()
    {
        if (_start == 0)
        {
            return;
        }

        Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
        _end -= _start;
        _start = 0;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "...";

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
