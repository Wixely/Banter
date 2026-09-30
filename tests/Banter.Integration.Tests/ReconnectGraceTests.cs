using System.Collections.Concurrent;
using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Persistence;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>
/// What happens to something you do in the gap between losing a connection and getting it back.
///
/// <para>Found on a phone. Android destroys an app's sockets once it has been out of the
/// foreground long enough, so the file picker — a trip out of the foreground by definition —
/// guaranteed that the upload it returned to was issued against a connection that was already
/// gone. The attachment was reported failed and dropped; a message typed in the same window
/// disappeared with no error at all. The client had reconnected a second later in both cases,
/// which is what makes losing the work indefensible rather than unlucky.</para>
///
/// <para>These drive that gap two ways: a server that goes away and comes back, and a socket cut
/// from under a transfer that is already running.</para>
/// </summary>
public sealed class ReconnectGraceTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("alice", "pw-a")
        .AddUser("bob", "pw-b");

    private string _dbPath = null!;
    private string _dataDir = null!;
    private BanterDatabase _database = null!;
    private Banter.Server.Files.FileStore _files = null!;
    private BanterServer _server = null!;

    /// <summary>
    /// Cuts the connection out from under the client after a set number of frames, so a drop can
    /// be placed in the middle of a transfer rather than waited for. Arms once: the redial gets a
    /// working connection, which is the point.
    /// </summary>
    private sealed class CuttingTransport(IBanterClientTransport inner) : IBanterClientTransport
    {
        private int _cutAfter = -1;
        private readonly ConcurrentDictionary<BanterMessageType, int> _sent = new();

        public void CutAfter(int frames) => Volatile.Write(ref _cutAfter, frames);

        /// <summary>
        /// How many frames of one kind this client has tried to send, the cut one included.
        ///
        /// <para>Decoding the envelope is what makes "did it resume or start again" answerable: both
        /// finish with the same file on the server, so the only difference is in what crossed the
        /// wire. A codec here is not a second implementation — it is the same one, read backwards.</para>
        /// </summary>
        public int Sent(BanterMessageType type) => _sent.TryGetValue(type, out var count) ? count : 0;

        private void Tally(ReadOnlyMemory<byte> frame)
        {
            try
            {
                _sent.AddOrUpdate(new BanterCodec().DecodeEnvelope(frame).Type, 1, (_, n) => n + 1);
            }
            catch (Exception)
            {
                // Not an envelope we can read. Counting is the only thing that suffers.
            }
        }

        public async Task<IBanterConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default) =>
            new Cutting(await inner.ConnectAsync(endpoint, cancellationToken), this);

        private sealed class Cutting(IBanterConnection inner, CuttingTransport owner) : IBanterConnection
        {
            public string RemoteDescription => inner.RemoteDescription;

            public async ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
            {
                owner.Tally(frame);

                if (Volatile.Read(ref owner._cutAfter) >= 0
                    && Interlocked.Decrement(ref owner._cutAfter) < 0)
                {
                    // Disarmed by going negative, so only this connection is cut. Disposing is
                    // what the platform does to a backgrounded app's socket: the far end sees a
                    // close, and this end fails whatever was mid-flight.
                    await inner.DisposeAsync();
                    throw new IOException("connection cut");
                }

                await inner.SendFrameAsync(frame, cancellationToken);
            }

            public ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default) =>
                inner.ReceiveFrameAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-grace-{id}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"banter-grace-files-{id}");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
        _files = new Banter.Server.Files.FileStore(
            _database, new Banter.Server.Files.FileStoreOptions { DataDirectory = _dataDir });
        _server = NewServer();
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();

        // The interrupted upload left a .part file open; without this the directory below cannot
        // be removed on Windows.
        await _files.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        File.Delete(_dbPath);
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task AMessageSentWhileTheServerIsAwayArrivesWhenItComesBack()
    {
        var port = _server.Endpoint.Port;
        await using var alice = await ConnectAsync("alice", "pw-a");
        await alice.JoinAsync("#grace");

        await StopServerAndWaitForDropAsync(alice);

        // The phone's moment: say something with no connection under it.
        var send = alice.SendMessageAsync("#grace", "held through the reconnect").AsTask();
        Assert.False(send.IsFaulted);

        await RestartServerAsync(port);
        await send.WaitAsync(Timeout);

        Assert.Contains(
            "held through the reconnect",
            await WaitForTextInRoomAsync("#grace", "held through the reconnect"));
    }

    [Fact]
    public async Task AnUploadStartedWhileTheServerIsAwayCompletesWhenItComesBack()
    {
        var port = _server.Endpoint.Port;
        await using var alice = await ConnectAsync("alice", "pw-a");
        await alice.JoinAsync("#grace-files");

        await StopServerAndWaitForDropAsync(alice);

        // 43 bytes: the size that failed on the phone, which ruled out every theory about
        // chunking and frame ceilings before this one.
        var content = new byte[43];
        Random.Shared.NextBytes(content);
        var upload = alice.UploadFileAsync("#grace-files", "held.bin", content, "application/octet-stream");
        Assert.False(upload.IsFaulted);

        await RestartServerAsync(port);
        var info = await upload.WaitAsync(Timeout);

        Assert.Equal("held.bin", info.Name);
        Assert.Equal(content.Length, info.Size);

        // A real file on the far side, not just an id: fetch the bytes back.
        Assert.Equal(content, await alice.DownloadFileAsync(info.FileId));
    }

    /// <summary>
    /// An upload cut part way through sends the REMAINDER, not the file again.
    ///
    /// <para>This test is older than the behaviour it now checks. It used to say there was nothing to
    /// resume against — chunks keyed to a session that had died — and assert only that the file
    /// eventually landed. That was true of an append-only upload: the server knew a byte count and
    /// nothing about whether those bytes were good, so the only safe answer was to start over. With a
    /// manifest it knows exactly which chunks it has verified, and can be asked.</para>
    ///
    /// <para>Landing the file is therefore no longer the interesting half, because starting again
    /// also lands it. What separates the two is what crossed the wire, so that is what is counted: a
    /// FILE_PUT_RESUME must have been asked, and the chunk frames must be about one fileful rather
    /// than two. Twenty chunks, cut after ten have gone: resuming sends about twenty-one in total and
    /// starting again sends about thirty-one.</para>
    /// </summary>
    [Fact]
    public async Task AnUploadCutPartWayThroughSendsOnlyTheRemainder()
    {
        var cutting = new CuttingTransport(_transport);
        await using var alice = await BanterClient.ConnectAsync(cutting, _server.Endpoint, "alice", "pw-a");
        await alice.JoinAsync("#grace-chunks");

        // Exactly twenty 64 KiB chunks, so the arithmetic below is not approximate.
        const int chunks = 20;
        var content = new byte[chunks * 64 * 1024];
        Random.Shared.NextBytes(content);

        // FILE_PUT_START and ten chunks get through; the eleventh is cut.
        cutting.CutAfter(11);

        var info = await alice
            .UploadFileAsync("#grace-chunks", "interrupted.bin", content, "application/octet-stream")
            .WaitAsync(Timeout);

        Assert.Equal(content, await alice.DownloadFileAsync(info.FileId));

        var chunkFrames = cutting.Sent(BanterMessageType.FilePutChunk);
        var resumes = cutting.Sent(BanterMessageType.FilePutResume);

        Assert.True(resumes >= 1, "the upload never asked what the server was still missing");
        Assert.InRange(chunkFrames, chunks, chunks + 5);
    }

    /// <summary>
    /// The chunks that were in flight when the connection went are not assumed lost.
    ///
    /// <para>A resume asks rather than remembering where it got to, and those are different answers:
    /// a chunk written to a socket that then died may well have arrived and been verified. Asking
    /// costs one round trip and is the only way to know; assuming would resend whatever was in flight,
    /// which on a slow link is the most expensive thing to be wrong about.</para>
    ///
    /// <para>Pinned here by the fact that the file lands correctly at all: a server that had accepted
    /// a chunk the client then re-sent must take the duplicate without complaint, because the bytes
    /// are the ones it already holds. Rejecting it as "already have that" would fail this upload.</para>
    /// </summary>
    [Fact]
    public async Task AResumedUploadMayResendAChunkTheServerAlreadyHas()
    {
        var cutting = new CuttingTransport(_transport);
        await using var alice = await BanterClient.ConnectAsync(cutting, _server.Endpoint, "alice", "pw-a");
        await alice.JoinAsync("#grace-dupes");

        var content = new byte[6 * 64 * 1024];
        Random.Shared.NextBytes(content);

        // Cut mid-transfer, then send every chunk the server says it wants - including any it took
        // from the dying connection, which the client cannot distinguish.
        cutting.CutAfter(3);

        var info = await alice
            .UploadFileAsync("#grace-dupes", "dupes.bin", content, "application/octet-stream")
            .WaitAsync(Timeout);

        Assert.Equal(content, await alice.DownloadFileAsync(info.FileId));
    }

    [Fact]
    public async Task ASendStillFailsWhenTheConnectionNeverComesBack()
    {
        // A short grace so the test does not sit out the ten-second default: what is pinned is
        // that the wait is bounded, not how long it is.
        await using var alice = await BanterClient.ConnectAsync(
            _transport, _server.Endpoint, "alice", "pw-a",
            new BanterClientOptions { ReconnectGrace = TimeSpan.FromMilliseconds(300) });
        await alice.JoinAsync("#gone");

        await StopServerAndWaitForDropAsync(alice);

        await Assert.ThrowsAsync<BanterDisconnectedException>(
            async () => await alice.SendMessageAsync("#gone", "into the void"));
    }

    [Fact]
    public async Task WaitingIsOptOutForCallersThatWantTheOldFailure()
    {
        await using var alice = await BanterClient.ConnectAsync(
            _transport, _server.Endpoint, "alice", "pw-a",
            new BanterClientOptions { ReconnectGrace = TimeSpan.Zero });
        await alice.JoinAsync("#nowait");

        await StopServerAndWaitForDropAsync(alice);

        await Assert.ThrowsAsync<BanterDisconnectedException>(
            async () => await alice.SendMessageAsync("#nowait", "no waiting here"));
    }

    private async Task StopServerAndWaitForDropAsync(BanterClient client)
    {
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => dropped.TrySetResult();
        await _server.DisposeAsync();
        await dropped.Task.WaitAsync(Timeout);
    }

    private BanterServer NewServer() =>
        new(_transport, _accounts, new DbServerStore(_database), _files);

    private Task<BanterClient> ConnectAsync(string user, string secret) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, secret);

    private async Task RestartServerAsync(int port)
    {
        _server = NewServer();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _server.StartAsync(new Uri($"tcp://127.0.0.1:{port}"));
                return;
            }
            catch (System.Net.Sockets.SocketException) when (attempt < 40)
            {
                await Task.Delay(100);
            }
        }
    }

    private async Task<IReadOnlyList<string>> TextsInRoomAsync(string room)
    {
        await using var bob = await ConnectAsync("bob", "pw-b");
        await bob.JoinAsync(room);
        var history = await bob.GetHistoryAsync(room);
        return [.. history.Messages.Select(m => m.Text)];
    }

    /// <summary>
    /// The room's messages, once the one being waited for is among them — or whatever is there
    /// when the timeout runs out, so the assertion still fails with a readable collection.
    ///
    /// <para>A send completing means this client got its acknowledgement. Whether another client
    /// can already read the row back is a separate question, and on a loaded CI runner the answer
    /// was sometimes "not yet": the read won, the collection came back empty, and a release tag
    /// failed on a test that had never failed on a developer machine. Waiting for the thing being
    /// asserted is the fix; sleeping a fixed amount would only move the race.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> WaitForTextInRoomAsync(string room, string text)
    {
        await using var bob = await ConnectAsync("bob", "pw-b");
        await bob.JoinAsync(room);

        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (true)
        {
            var history = await bob.GetHistoryAsync(room);
            var texts = history.Messages.Select(m => m.Text).ToList();

            if (texts.Contains(text) || DateTimeOffset.UtcNow >= deadline)
            {
                return texts;
            }

            await Task.Delay(50);
        }
    }
}
