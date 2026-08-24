using Banter.Protocol;
using Xunit;

namespace Banter.Protocol.Tests;

public sealed class FramingTests
{
    [Fact]
    public async Task FramesRoundTripInOrderAndEndCleanly()
    {
        var codec = new BanterCodec();
        var frames = new[]
        {
            codec.EncodeEnvelope(codec.CreateEnvelope(new JoinPayload("#main"))),
            codec.EncodeEnvelope(codec.CreateEnvelope(new MsgPayload("#main", "alice", "hi", 1, null))),
            codec.EncodeEnvelope(codec.CreateEnvelope(new ByePayload(null))),
        };

        using var stream = new MemoryStream();
        foreach (var frame in frames)
        {
            await BanterFraming.WriteFrameAsync(stream, frame);
        }

        stream.Position = 0;
        foreach (var expected in frames)
        {
            var actual = await BanterFraming.ReadFrameAsync(stream);
            Assert.NotNull(actual);
            Assert.Equal(expected, actual);
        }

        Assert.Null(await BanterFraming.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task EmptyFrameIsLegal()
    {
        using var stream = new MemoryStream();
        await BanterFraming.WriteFrameAsync(stream, Array.Empty<byte>());
        stream.Position = 0;

        var frame = await BanterFraming.ReadFrameAsync(stream);
        Assert.NotNull(frame);
        Assert.Empty(frame);
    }

    [Fact]
    public async Task OversizeFrameIsRejectedBeforeAllocation()
    {
        using var stream = new MemoryStream();
        await BanterFraming.WriteFrameAsync(stream, new byte[64]);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await BanterFraming.ReadFrameAsync(stream, maxFrameBytes: 16));
    }

    [Fact]
    public async Task TruncationInsidePrefixThrows()
    {
        using var stream = new MemoryStream([0x08, 0x00]);
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await BanterFraming.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task TruncationInsideBodyThrows()
    {
        using var full = new MemoryStream();
        await BanterFraming.WriteFrameAsync(full, new byte[32]);
        using var truncated = new MemoryStream(full.ToArray()[..20]);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await BanterFraming.ReadFrameAsync(truncated));
    }
}
