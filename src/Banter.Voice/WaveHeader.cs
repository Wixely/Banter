using System.Buffers.Binary;

namespace Banter.Voice;

/// <summary>What a WAV header says the samples after it are.</summary>
public readonly record struct WaveFormat(int SampleRate, int Channels, int BitsPerSample);

/// <summary>
/// Reads a RIFF/WAVE header. <c>PcmAudio.CreateWaveStream</c> writes one for the microphone
/// direction; this is the other way round, for speech servers that answer with a WAV rather than
/// raw samples — most local ones do, and the header is the only place the sample rate is stated.
/// </summary>
public static class WaveHeader
{
    /// <summary>Smallest header a canonical <c>fmt </c> + <c>data</c> file can have.</summary>
    public const int MinimumBytes = 44;

    /// <summary>
    /// Parses <paramref name="header"/>, reporting the format and where the samples start.
    ///
    /// <para>Walks the chunk list rather than assuming the canonical 44-byte layout: servers
    /// interpose <c>LIST</c> and <c>fact</c> chunks, and a reader that trusts the offset plays
    /// the metadata as audio.</para>
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> header, out WaveFormat format, out int dataOffset)
    {
        format = default;
        dataOffset = 0;

        if (header.Length < 12
            || !header[..4].SequenceEqual("RIFF"u8)
            || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var haveFormat = false;
        var position = 12;

        while (position + 8 <= header.Length)
        {
            var id = header.Slice(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(position + 4, 4));
            var body = position + 8;

            if (id.SequenceEqual("fmt "u8))
            {
                if (body + 16 > header.Length)
                {
                    return false;
                }

                format = new WaveFormat(
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(body + 4, 4)),
                    BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(body + 2, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(body + 14, 2)));
                haveFormat = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                // A server streaming a WAV cannot know the length in advance and writes 0 or
                // 0xFFFFFFFF here, so the declared size is ignored: the stream ending is what
                // ends the audio.
                dataOffset = body;
                return haveFormat;
            }

            // Chunks are word-aligned, so an odd size is followed by a pad byte. Widened to long
            // first: a corrupt size otherwise wraps the position back into the header.
            var next = (long)body + size + (size & 1);
            if (next > header.Length)
            {
                return false;
            }

            position = (int)next;
        }

        return false;
    }
}
