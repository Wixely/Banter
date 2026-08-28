using System.Threading.Channels;
using Banter.Transport.Shrine;
using Xunit;

namespace Banter.Transport.Shrine.Tests;

/// <summary>A conduit under the test's control, standing in for a SiteSession.</summary>
internal sealed class FakeFrames : IShrineFrames
{
    private readonly Channel<byte[]?> _inbound = Channel.CreateUnbounded<byte[]?>();

    public List<byte[]> Sent { get; } = [];

    public string? EndedWith { get; private set; }

    public int Ends { get; private set; }

    public int MaxFrameBytes { get; init; } = 196608;

    public string? EndReason { get; set; }

    /// <summary>Queues a frame for the connection to receive; null ends the stream.</summary>
    public void Deliver(byte[]? frame) => _inbound.Writer.TryWrite(frame);

    public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        lock (Sent)
        {
            Sent.Add(frame.ToArray());
        }

        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default) =>
        await _inbound.Reader.ReadAsync(cancellationToken);

    public Task EndAsync(string reason, CancellationToken cancellationToken = default)
    {
        Ends++;
        EndedWith = reason;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The conduit presented as an <see cref="Banter.Protocol.Transport.IBanterConnection"/>. Thin by
/// design — the rite already serialises sends and latches a clean close — so what is tested here
/// is mostly the one thing Banter must add: refusing a frame the conduit cannot carry.
/// </summary>
public sealed class ShrineConnectionTests
{
    [Fact]
    public async Task FramesGoOutAndComeBack()
    {
        var frames = new FakeFrames();
        await using var connection = new ShrineConnection(frames, "pilgrim");

        await connection.SendFrameAsync(new byte[] { 1, 2, 3 });
        frames.Deliver([4, 5, 6]);

        Assert.Equal([1, 2, 3], Assert.Single(frames.Sent));
        Assert.Equal([4, 5, 6], await connection.ReceiveFrameAsync());
    }

    [Fact]
    public async Task ACleanCloseIsNull()
    {
        var frames = new FakeFrames();
        await using var connection = new ShrineConnection(frames, "pilgrim");

        frames.Deliver(null);

        Assert.Null(await connection.ReceiveFrameAsync());
    }

    [Fact]
    public async Task AFrameTooBigForTheConduitIsRefusedByName()
    {
        var frames = new FakeFrames { MaxFrameBytes = 1024 };
        await using var connection = new ShrineConnection(frames, "pilgrim");

        // BanterProtocol declares no bound on a frame and its own ceiling is 4 MB, so this is the
        // one place the conduit's limit is visible. A history page is the realistic offender.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.SendFrameAsync(new byte[2048]).AsTask());

        Assert.Contains("2048", error.Message);
        Assert.Contains("1024", error.Message);
        Assert.Contains("relic", error.Message);
        Assert.Empty(frames.Sent);
    }

    [Fact]
    public async Task AFrameExactlyAtTheLimitIsAllowed()
    {
        var frames = new FakeFrames { MaxFrameBytes = 1024 };
        await using var connection = new ShrineConnection(frames, "pilgrim");

        await connection.SendFrameAsync(new byte[1024]);

        Assert.Single(frames.Sent);
    }

    [Fact]
    public async Task TheConduitsLimitIsReadRatherThanAssumed()
    {
        // It differs between the WebRTC and Arcanum channel paths, so nothing may hard-code it.
        var frames = new FakeFrames { MaxFrameBytes = 65536 };
        await using var connection = new ShrineConnection(frames, "pilgrim");

        Assert.Equal(65536, connection.MaxFrameBytes);
    }

    [Fact]
    public async Task DisposingEndsTheSession()
    {
        var frames = new FakeFrames();
        var connection = new ShrineConnection(frames, "pilgrim");

        await connection.DisposeAsync();

        Assert.Equal(1, frames.Ends);
    }

    [Fact]
    public async Task WhyThePeerLeftIsAvailable()
    {
        var frames = new FakeFrames { EndReason = "unknown protocol" };
        await using var connection = new ShrineConnection(frames, "pilgrim");

        Assert.Equal("unknown protocol", connection.EndReason);
    }
}

/// <summary>
/// The push-to-pull bridge. Nodestar ends a session the moment its handler returns, so the
/// handler has to stay parked for the life of the connection — get that wrong and a visitor is
/// disconnected the instant they arrive, while the server still holds a connection it believes is
/// live. These are the tests that would catch it.
/// </summary>
public sealed class ShrineBanterListenerTests
{
    private static readonly Uri Endpoint = new("cupri://example/banter");

    private static async Task<bool> CompletesAsync(Task task, int millisecondsTimeout = 1000) =>
        await Task.WhenAny(task, Task.Delay(millisecondsTimeout)) == task;

    [Fact]
    public async Task AnArrivingSessionBecomesAConnection()
    {
        await using var listener = new ShrineBanterListener(Endpoint);
        var frames = new FakeFrames();

        var handler = listener.HandleSessionAsync(frames, "pilgrim-1", CancellationToken.None);
        await using var connection = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("pilgrim-1", connection.RemoteDescription);
        Assert.False(handler.IsCompleted);
    }

    [Fact]
    public async Task TheHandlerStaysParkedWhileTheConnectionIsInUse()
    {
        await using var listener = new ShrineBanterListener(Endpoint);
        var frames = new FakeFrames();

        var handler = listener.HandleSessionAsync(frames, "pilgrim", CancellationToken.None);
        var connection = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Returning here would end the session under a connection the server is still using.
        Assert.False(await CompletesAsync(handler, 200));

        await connection.DisposeAsync();

        Assert.True(await CompletesAsync(handler), "the handler should return once the connection is disposed");
    }

    [Fact]
    public async Task TheConnectionWorksWhileTheHandlerIsParked()
    {
        await using var listener = new ShrineBanterListener(Endpoint);
        var frames = new FakeFrames();

        _ = listener.HandleSessionAsync(frames, "pilgrim", CancellationToken.None);
        await using var connection = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await connection.SendFrameAsync(new byte[] { 9 });
        frames.Deliver([8]);

        Assert.Equal([9], Assert.Single(frames.Sent));
        Assert.Equal([8], await connection.ReceiveFrameAsync());
    }

    [Fact]
    public async Task SeveralVisitorsAreAcceptedInTurn()
    {
        await using var listener = new ShrineBanterListener(Endpoint);

        var handlers = Enumerable.Range(0, 3)
            .Select(i => listener.HandleSessionAsync(new FakeFrames(), $"pilgrim-{i}", CancellationToken.None))
            .ToArray();

        var accepted = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var connection = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
            accepted.Add(connection.RemoteDescription);
        }

        Assert.Equal(["pilgrim-0", "pilgrim-1", "pilgrim-2"], accepted);
        Assert.All(handlers, h => Assert.False(h.IsCompleted));
    }

    [Fact]
    public async Task DisposingTheListenerReleasesEveryParkedHandler()
    {
        var listener = new ShrineBanterListener(Endpoint);

        var accepted = listener.HandleSessionAsync(new FakeFrames(), "accepted", CancellationToken.None);
        await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Queued but never accepted — it must not be left holding a session open either.
        var queued = listener.HandleSessionAsync(new FakeFrames(), "queued", CancellationToken.None);

        await listener.DisposeAsync();

        Assert.True(await CompletesAsync(accepted), "an accepted session's handler should be released");
        Assert.True(await CompletesAsync(queued), "a queued session's handler should be released");
    }

    [Fact]
    public async Task AcceptingAfterDisposalSaysSo()
    {
        var listener = new ShrineBanterListener(Endpoint);
        await listener.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => listener.AcceptAsync());
    }

    [Fact]
    public async Task CancellingTheNodesTokenReleasesTheHandler()
    {
        await using var listener = new ShrineBanterListener(Endpoint);
        using var nodeStopping = new CancellationTokenSource();

        var handler = listener.HandleSessionAsync(new FakeFrames(), "pilgrim", nodeStopping.Token);
        await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // The node shutting down must not leave the handler parked on a connection nobody will
        // dispose.
        await nodeStopping.CancelAsync();

        Assert.True(await CompletesAsync(handler));
    }

    [Fact]
    public async Task AcceptHonoursItsOwnCancellation()
    {
        await using var listener = new ShrineBanterListener(Endpoint);
        using var giveUp = new CancellationTokenSource(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.AcceptAsync(giveUp.Token));
    }
}
