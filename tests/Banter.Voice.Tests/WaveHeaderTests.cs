using System.Buffers.Binary;
using System.Text;
using Bantz.Speech;
using Xunit;
using static Banter.Voice.Tests.Pcm;

namespace Banter.Voice.Tests;

/// <summary>
/// The header is the only place a WAV states its sample rate, and getting it wrong plays
/// everything an agent says at the wrong pitch — so these cover the shapes real servers emit as
/// much as the canonical one.
/// </summary>
public sealed class WaveHeaderTests
{
    /// <summary>A header written by the same code that writes ours, so the round trip is real.</summary>
    private static byte[] RealWave(int sampleRate = 24000, int channels = 1)
    {
        using var stream = new PcmAudio(Speaking(Ms(100)), sampleRate, channels).CreateWaveStream();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Assembles a RIFF file from chunks, for the layouts Bantz's writer never produces.</summary>
    private static byte[] Riff(params (string Id, byte[] Body)[] chunks)
    {
        var body = new MemoryStream();
        foreach (var (id, content) in chunks)
        {
            body.Write(Encoding.ASCII.GetBytes(id));
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)content.Length);
            body.Write(size);
            body.Write(content);
            if (content.Length % 2 == 1)
            {
                body.WriteByte(0);                          // chunks are word-aligned
            }
        }

        var file = new MemoryStream();
        file.Write("RIFF"u8);
        var riffSize = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(riffSize, (uint)(body.Length + 4));
        file.Write(riffSize);
        file.Write("WAVE"u8);
        file.Write(body.ToArray());
        return file.ToArray();
    }

    private static byte[] Fmt(int sampleRate = 24000, int channels = 1, int bits = 16)
    {
        var fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(0), 1);                     // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)(sampleRate * channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bits);
        return fmt;
    }

    [Fact]
    public void AHeaderWrittenByTheCaptureSideIsReadBack()
    {
        var wave = RealWave(sampleRate: 24000);

        Assert.True(WaveHeader.TryParse(wave, out var format, out var dataOffset));
        Assert.Equal(new WaveFormat(24000, 1, 16), format);
        Assert.Equal(44, dataOffset);
    }

    [Fact]
    public void StereoAndOtherRatesAreReadAsWritten()
    {
        Assert.True(WaveHeader.TryParse(RealWave(48000, 2), out var format, out _));

        Assert.Equal(48000, format.SampleRate);
        Assert.Equal(2, format.Channels);
    }

    [Fact]
    public void MetadataChunksBeforeTheAudioAreSteppedOver()
    {
        // A reader that trusted the canonical 44-byte offset would play this as audio.
        var wave = Riff(
            ("fmt ", Fmt(22050)),
            ("LIST", Encoding.ASCII.GetBytes("INFOISFTPiper")),
            ("data", [1, 2, 3, 4]));

        Assert.True(WaveHeader.TryParse(wave, out var format, out var dataOffset));
        Assert.Equal(22050, format.SampleRate);
        Assert.Equal(4, wave.Length - dataOffset);
    }

    [Fact]
    public void AnOddSizedChunkIsFollowedByAPadByte()
    {
        var wave = Riff(("fmt ", Fmt()), ("fact", [7, 7, 7]), ("data", [9, 9]));

        Assert.True(WaveHeader.TryParse(wave, out _, out var dataOffset));
        Assert.Equal(2, wave.Length - dataOffset);
    }

    [Fact]
    public void AStreamingServersUnknownLengthIsNotTakenSeriously()
    {
        // Length cannot be known before synthesis finishes, so servers write a placeholder here.
        var wave = Riff(("fmt ", Fmt()), ("data", []));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(wave.Length - 4), uint.MaxValue);

        Assert.True(WaveHeader.TryParse(wave, out _, out var dataOffset));
        Assert.Equal(wave.Length, dataOffset);
    }

    [Fact]
    public void SomethingThatIsNotARiffFileIsRefused()
    {
        Assert.False(WaveHeader.TryParse(Encoding.ASCII.GetBytes("<html>502 Bad Gateway</html>"), out _, out _));
    }

    [Fact]
    public void ATruncatedHeaderIsRefusedRatherThanGuessedAt()
    {
        var wave = RealWave();

        Assert.False(WaveHeader.TryParse(wave.AsSpan(0, 20), out _, out _));
        Assert.False(WaveHeader.TryParse([], out _, out _));
    }

    [Fact]
    public void ADataChunkWithNoFormatBeforeItIsRefused()
    {
        Assert.False(WaveHeader.TryParse(Riff(("data", [1, 2])), out _, out _));
    }

    [Fact]
    public void ACorruptChunkSizeDoesNotWalkBackwardsForever()
    {
        var wave = Riff(("fmt ", Fmt()), ("data", [1, 2]));

        // A size that wraps int arithmetic would send the cursor back into the header and loop.
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), uint.MaxValue);

        Assert.False(WaveHeader.TryParse(wave, out _, out _));
    }
}
