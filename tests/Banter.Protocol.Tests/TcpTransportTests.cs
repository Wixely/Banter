using Banter.Protocol;
using Banter.Protocol.Transport;
using Xunit;

namespace Banter.Protocol.Tests;

public sealed class TcpTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FramesFlowBothWaysOverLoopback()
    {
        var transport = new TcpBanterTransport();
        await using var listener = await transport.ListenAsync(new Uri("tcp://127.0.0.1:0"));

        var acceptTask = listener.AcceptAsync();
        await using var clientSide = await transport.ConnectAsync(listener.LocalEndpoint).WaitAsync(Timeout);
        await using var serverSide = await acceptTask.WaitAsync(Timeout);

        var codec = new BanterCodec();
        var hello = codec.EncodeEnvelope(codec.CreateEnvelope(new HelloPayload("test", "0.1.0", [])));
        var pong = codec.EncodeEnvelope(codec.CreateEnvelope(new PongPayload(42)));

        await clientSide.SendFrameAsync(hello);
        Assert.Equal(hello, await serverSide.ReceiveFrameAsync().AsTask().WaitAsync(Timeout));

        await serverSide.SendFrameAsync(pong);
        Assert.Equal(pong, await clientSide.ReceiveFrameAsync().AsTask().WaitAsync(Timeout));
    }

    [Fact]
    public async Task PeerDisposalYieldsNullReceive()
    {
        var transport = new TcpBanterTransport();
        await using var listener = await transport.ListenAsync(new Uri("tcp://127.0.0.1:0"));

        var acceptTask = listener.AcceptAsync();
        var clientSide = await transport.ConnectAsync(listener.LocalEndpoint).WaitAsync(Timeout);
        await using var serverSide = await acceptTask.WaitAsync(Timeout);

        await clientSide.DisposeAsync();
        Assert.Null(await serverSide.ReceiveFrameAsync().AsTask().WaitAsync(Timeout));
    }

    [Fact]
    public async Task PortZeroBindsToARealPort()
    {
        var transport = new TcpBanterTransport();
        await using var listener = await transport.ListenAsync(new Uri("tcp://127.0.0.1:0"));
        Assert.NotEqual(0, listener.LocalEndpoint.Port);
    }

    [Fact]
    public async Task WrongSchemeIsRejected()
    {
        var transport = new TcpBanterTransport();
        await Assert.ThrowsAsync<ArgumentException>(() => transport.ConnectAsync(new Uri("ws://127.0.0.1:1")));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.ListenAsync(new Uri("cuprinet://intone/abc")));
    }

    [Fact]
    public async Task ConcurrentSendsDoNotInterleaveFrames()
    {
        var transport = new TcpBanterTransport();
        await using var listener = await transport.ListenAsync(new Uri("tcp://127.0.0.1:0"));

        var acceptTask = listener.AcceptAsync();
        await using var clientSide = await transport.ConnectAsync(listener.LocalEndpoint).WaitAsync(Timeout);
        await using var serverSide = await acceptTask.WaitAsync(Timeout);

        const int sendersCount = 8;
        const int framesPerSender = 50;
        var sends = Enumerable.Range(0, sendersCount).Select(sender => Task.Run(async () =>
        {
            var frame = new byte[256];
            Array.Fill(frame, (byte)sender);
            for (var i = 0; i < framesPerSender; i++)
            {
                await clientSide.SendFrameAsync(frame);
            }
        })).ToArray();

        var received = 0;
        while (received < sendersCount * framesPerSender)
        {
            var frame = await serverSide.ReceiveFrameAsync().AsTask().WaitAsync(Timeout);
            Assert.NotNull(frame);
            Assert.Equal(256, frame.Length);
            // Every byte in a frame must match its first byte — interleaved writes would mix senders.
            Assert.All(frame, b => Assert.Equal(frame[0], b));
            received++;
        }

        await Task.WhenAll(sends);
    }
}
