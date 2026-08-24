using System.Buffers.Binary;

namespace Banter.Protocol;

/// <summary>
/// Length-prefixed framing for stream-oriented transports: 4-byte little-endian payload length,
/// then the payload. CupriNet channels that are message-oriented don't need this layer; the
/// TCP/TLS fallback transport does.
/// </summary>
public static class BanterFraming
{
    /// <summary>Default ceiling. File transfer chunks at ~64 KB, so a frame near this size is
    /// a protocol violation, not a big upload.</summary>
    public const int DefaultMaxFrameBytes = 4 * 1024 * 1024;

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, frame.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame. Returns null on a clean end-of-stream at a frame boundary;
    /// throws on truncation mid-frame or an oversize/negative length prefix.</summary>
    public static async ValueTask<byte[]?> ReadFrameAsync(
        Stream stream,
        int maxFrameBytes = DefaultMaxFrameBytes,
        CancellationToken cancellationToken = default)
    {
        var prefix = new byte[4];
        var read = await ReadAtMostAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < prefix.Length)
        {
            throw new EndOfStreamException("Stream ended inside a frame length prefix.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length < 0 || length > maxFrameBytes)
        {
            throw new InvalidDataException($"Frame length {length} is outside [0, {maxFrameBytes}].");
        }

        var frame = new byte[length];
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        return frame;
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
