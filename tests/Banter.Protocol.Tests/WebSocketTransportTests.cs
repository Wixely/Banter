using Banter.Protocol;
using Banter.Protocol.Transport;
using Xunit;

namespace Banter.Protocol.Tests;

/// <summary>
/// The transport a browser has to use, since script cannot open a socket (PLAN §2.5). Mirrors the
/// TCP suite, plus the two things WebSocket changes: messages arrive whole rather than needing a
/// length prefix, and a message may still span several reads.
/// </summary>
public sealed class WebSocketTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A free loopback port. HttpListener cannot bind port 0 and report back what it got, so
    /// unlike the TCP suite the port has to be chosen before binding.
    /// </summary>
    private static Uri FreeEndpoint()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new Uri($"ws://localhost:{port}/");
    }

    [Fact]
    public async Task FramesFlowBothWaysOverLoopback()
    {
        var transport = new WebSocketBanterTransport();
        await using var listener = await transport.ListenAsync(FreeEndpoint());

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
        var transport = new WebSocketBanterTransport();
        await using var listener = await transport.ListenAsync(FreeEndpoint());

        var acceptTask = listener.AcceptAsync();
        var clientSide = await transport.ConnectAsync(listener.LocalEndpoint).WaitAsync(Timeout);
        await using var serverSide = await acceptTask.WaitAsync(Timeout);

        await clientSide.DisposeAsync();
        Assert.Null(await serverSide.ReceiveFrameAsync().AsTask().WaitAsync(Timeout));
    }

    [Fact]
    public async Task AFrameLargerThanOneReadArrivesWhole()
    {
        var transport = new WebSocketBanterTransport();
        await using var listener = await transport.ListenAsync(FreeEndpoint());

        var acceptTask = listener.AcceptAsync();
        await using var clientSide = await transport.ConnectAsync(listener.LocalEndpoint).WaitAsync(Timeout);
        await using var serverSide = await acceptTask.WaitAsync(Timeout);

        // Comfortably past the 16 KB receive scratch, so the reassembly loop is what is under test
        // rather than a single lucky read. An uploaded image is this shape.
        var big = new byte[600 * 1024];
        Random.Shared.NextBytes(big);

        await clientSide.SendFrameAsync(big);
        var received = await serverSide.ReceiveFrameAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal(big, received);
    }

    [Fact]
    public async Task AnEmptyFrameIsAFrameRatherThanAClose()
    {
        var transport = new WebSocketBanterTransport();
        await using var listener = await transport.ListenAsync(FreeEndpoint());

        var acceptTask = listener.AcceptAsync();
        await using var clientSide = await transport.ConnectAsync(listener.LocalEndpoint).WaitAsync(Timeout);
        await using var serverSide = await acceptTask.WaitAsync(Timeout);

        await clientSide.SendFrameAsync(ReadOnlyMemory<byte>.Empty);
        var received = await serverSide.ReceiveFrameAsync().AsTask().WaitAsync(Timeout);

        // Null means the peer went away; an empty payload must not be mistaken for that.
        Assert.NotNull(received);
        Assert.Empty(received);
    }

    [Fact]
    public async Task WrongSchemeIsRejected()
    {
        var transport = new WebSocketBanterTransport();
        await Assert.ThrowsAsync<ArgumentException>(() => transport.ConnectAsync(new Uri("tcp://127.0.0.1:1")));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.ListenAsync(new Uri("cuprinet://intone/abc")));
    }

    [Fact]
    public async Task ConcurrentSendsDoNotInterleaveFrames()
    {
        var transport = new WebSocketBanterTransport();
        await using var listener = await transport.ListenAsync(FreeEndpoint());

        var acceptTask = listener.AcceptAsync();
        await using var clientSide = await transport.ConnectAsync(listener.LocalEndpoint).WaitAsync(Timeout);
        await using var serverSide = await acceptTask.WaitAsync(Timeout);

        const int sendersCount = 8;
        const int framesPerSender = 40;
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

            // WebSocket.SendAsync forbids overlapping sends, and two that overlap interleave into
            // one corrupt message rather than failing — so this is what the send lock is for.
            Assert.Equal(256, frame.Length);
            Assert.All(frame, b => Assert.Equal(frame[0], b));
            received++;
        }

        await Task.WhenAll(sends);
    }

    [Fact]
    public async Task SomethingThatIsNotAnUpgradeIsRefusedWithoutTakingTheAccept()
    {
        var transport = new WebSocketBanterTransport();
        var endpoint = FreeEndpoint();
        await using var listener = await transport.ListenAsync(endpoint);

        var acceptTask = listener.AcceptAsync();

        // A health probe, or a browser pointed at the port. It must not occupy the accept and
        // leave the next real client waiting behind it.
        using var http = new HttpClient();
        var probe = await http.GetAsync($"http://localhost:{endpoint.Port}/").WaitAsync(Timeout);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, probe.StatusCode);

        await using var clientSide = await transport.ConnectAsync(endpoint).WaitAsync(Timeout);
        await using var serverSide = await acceptTask.WaitAsync(Timeout);

        await clientSide.SendFrameAsync(new byte[] { 1, 2, 3 });
        Assert.Equal([1, 2, 3], await serverSide.ReceiveFrameAsync().AsTask().WaitAsync(Timeout));
    }

    [Fact]
    public async Task TheRemoteIsDescribedForLogs()
    {
        var transport = new WebSocketBanterTransport();
        await using var listener = await transport.ListenAsync(FreeEndpoint());

        var acceptTask = listener.AcceptAsync();
        await using var clientSide = await transport.ConnectAsync(listener.LocalEndpoint).WaitAsync(Timeout);
        await using var serverSide = await acceptTask.WaitAsync(Timeout);

        Assert.NotEmpty(clientSide.RemoteDescription);
        Assert.NotEmpty(serverSide.RemoteDescription);
    }
}
