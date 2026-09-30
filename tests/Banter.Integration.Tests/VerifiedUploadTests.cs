using System.Security.Cryptography;
using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>
/// Uploads that carry a manifest (PLAN §5a), at the store rather than over a socket.
///
/// <para>The end-to-end proof that a dropped connection now costs the remainder rather than the file
/// is in <c>ReconnectGraceTests</c>. What a socket cannot reach is the arithmetic underneath: a
/// corrupted chunk, a chunk arriving twice or out of order, an upload finished early, and a manifest
/// that does not describe the file it claims to. Each of those is a way to end up with a blob whose
/// bytes are not the bytes anybody sent, which is the failure worth being certain about.</para>
/// </summary>
public sealed class VerifiedUploadTests : IAsyncLifetime
{
    private const int ChunkBytes = 64 * 1024;

    private string _dbPath = null!;
    private string _dataDir = null!;
    private BanterDatabase _database = null!;
    private FileStore _files = null!;

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-vup-{id}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"banter-vup-data-{id}");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
        _files = new FileStore(_database, new FileStoreOptions { DataDirectory = _dataDir });
    }

    public async Task DisposeAsync()
    {
        // Uploads left deliberately unfinished hold their .part handle open.
        await _files.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        File.Delete(_dbPath);
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private static byte[] Noise(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    private static int ChunkCount(int length) => (length + ChunkBytes - 1) / ChunkBytes;

    /// <summary>The manifest a client would send: a hash per chunk, in order.</summary>
    private static byte[][] Describe(byte[] content)
    {
        var hashes = new byte[ChunkCount(content.Length)][];
        for (var i = 0; i < hashes.Length; i++)
        {
            hashes[i] = SHA256.HashData(Slice(content, i));
        }

        return hashes;
    }

    private static byte[] Slice(byte[] content, int index)
    {
        var offset = index * ChunkBytes;
        return content[offset..Math.Min(offset + ChunkBytes, content.Length)];
    }

    private FilePutStartPayload Start(byte[] content, string name = "verified.bin") => new(
        "#room", name, "application/octet-stream", content.Length,
        Convert.ToHexStringLower(SHA256.HashData(content)), null, true,
        ChunkBytes, Describe(content));

    private Task SendAsync(string fileId, byte[] content, int index) =>
        _files.AppendChunkAsync("alice", new FilePutChunkPayload(fileId, (long)index * ChunkBytes, Slice(content, index)));

    /// <summary>
    /// The whole stored file, read the way a client reads it — in a loop until EOF.
    ///
    /// <para>Not one big read: the store clamps a read to its own <c>MaxChunkBytes</c>, so asking for
    /// a file larger than that returns a short buffer rather than refusing, and an assertion against
    /// it fails looking exactly like a truncated upload.</para>
    /// </summary>
    private async Task<byte[]> ReadAllAsync(string fileId)
    {
        using var buffer = new MemoryStream();
        long offset = 0;
        while (true)
        {
            var chunk = await _files.ReadChunkAsync(fileId, offset, ChunkBytes);
            buffer.Write(chunk.Data);
            offset += chunk.Data.Length;
            if (chunk.Eof)
            {
                return buffer.ToArray();
            }
        }
    }

    /// <summary>
    /// A corrupted chunk is refused as it lands, and the upload survives being told so.
    ///
    /// <para>This is the whole point of a manifest. Without one the bytes go straight into the file
    /// and the only check is the whole-file hash at the end — so a single flipped byte is discovered
    /// after the entire file has crossed the wire, and the only recovery is to send all of it again.
    /// Here the bad chunk is the only casualty: it is named, nothing already accepted is lost, and
    /// sending the right bytes for that one index finishes the upload.</para>
    /// </summary>
    [Fact]
    public async Task ACorruptedChunkIsRefusedAndTheUploadSurvivesIt()
    {
        var content = Noise(3 * ChunkBytes);
        var (info, _) = await _files.StartUploadAsync("alice", Start(content));

        await SendAsync(info.FileId, content, 0);

        // One byte different, which is the case a length check cannot catch.
        var tampered = Slice(content, 1);
        tampered[17] ^= 0xFF;
        var refused = await Assert.ThrowsAsync<FileStoreException>(() => _files.AppendChunkAsync(
            "alice", new FilePutChunkPayload(info.FileId, ChunkBytes, tampered)));
        Assert.Equal("BAD_CHUNK", refused.Code);

        // Still open, still holding chunk 0, and still wanting exactly 1 and 2.
        Assert.Equal([1, 2], await _files.MissingChunksAsync("alice", info.FileId));

        await SendAsync(info.FileId, content, 1);
        await SendAsync(info.FileId, content, 2);
        var (done, _, _) = await _files.FinalizeAsync("alice", info.FileId);

        Assert.True(done.Complete);
        Assert.Equal(content, await ReadAllAsync(done.FileId));
    }

    /// <summary>
    /// Order stops mattering, because an offset names a chunk rather than the end of the file.
    ///
    /// <para>Not a convenience: it is the same property resume depends on. A server that could only
    /// append would have to refuse anything but the next chunk, which is why the append-only path
    /// cannot be resumed even though it knows how many bytes it has.</para>
    /// </summary>
    [Fact]
    public async Task ChunksMayArriveInAnyOrder()
    {
        var content = Noise((4 * ChunkBytes) + 991);
        var (info, _) = await _files.StartUploadAsync("alice", Start(content));

        foreach (var index in Enumerable.Range(0, ChunkCount(content.Length)).Reverse())
        {
            await SendAsync(info.FileId, content, index);
        }

        var (done, _, _) = await _files.FinalizeAsync("alice", info.FileId);
        Assert.Equal(content, await ReadAllAsync(done.FileId));
    }

    /// <summary>
    /// The same chunk twice is accepted, and counted once.
    ///
    /// <para>A resume cannot know whether the chunks in flight when the connection went arrived, so it
    /// re-sends what the server says it wants and may well include one it already holds. Those bytes
    /// verified against the manifest, so they are the bytes on disk; treating the duplicate as an
    /// error would fail an upload for being careful.</para>
    /// </summary>
    [Fact]
    public async Task TheSameChunkTwiceIsHarmless()
    {
        var content = Noise(2 * ChunkBytes);
        var (info, _) = await _files.StartUploadAsync("alice", Start(content));

        await SendAsync(info.FileId, content, 0);
        await SendAsync(info.FileId, content, 0);
        await SendAsync(info.FileId, content, 1);

        var (done, _, _) = await _files.FinalizeAsync("alice", info.FileId);
        Assert.Equal(content.Length, done.Size);
        Assert.Equal(content, await ReadAllAsync(done.FileId));
    }

    /// <summary>
    /// Finishing early is named, rather than producing a file.
    ///
    /// <para>The scratch file is created at its full length, so a premature finalise has a
    /// correctly-sized file full of zeroes where the missing chunks go. The whole-file hash would
    /// catch that — but it would report HASH_MISMATCH, which says "your bytes were wrong" to someone
    /// whose bytes were fine and merely incomplete, and it destroys the upload instead of letting it
    /// be finished. So completeness is checked first and answered with the count and a pointer at
    /// FILE_PUT_RESUME.</para>
    /// </summary>
    [Fact]
    public async Task FinishingBeforeEveryChunkHasArrivedIsNamedAndSurvivable()
    {
        var content = Noise(3 * ChunkBytes);
        var (info, _) = await _files.StartUploadAsync("alice", Start(content));

        await SendAsync(info.FileId, content, 0);
        await SendAsync(info.FileId, content, 2);

        var early = await Assert.ThrowsAsync<FileStoreException>(() => _files.FinalizeAsync("alice", info.FileId));
        Assert.Equal("INCOMPLETE", early.Code);
        Assert.Contains("FILE_PUT_RESUME", early.Message, StringComparison.Ordinal);

        // And it really was survivable: the one missing chunk finishes it.
        Assert.Equal([1], await _files.MissingChunksAsync("alice", info.FileId));
        await SendAsync(info.FileId, content, 1);
        var (done, _, _) = await _files.FinalizeAsync("alice", info.FileId);
        Assert.Equal(content, await ReadAllAsync(done.FileId));
    }

    /// <summary>
    /// An offset that is not a chunk boundary is refused rather than guessed at.
    ///
    /// <para>On this path the offset IS the chunk index, so a misaligned one is a client disagreeing
    /// with the manifest it sent. Rounding to the nearest chunk would write verified bytes to the
    /// wrong place, which is the one failure a manifest is supposed to make impossible.</para>
    /// </summary>
    [Fact]
    public async Task AnOffsetThatIsNotAChunkBoundaryIsRefused()
    {
        var content = Noise(2 * ChunkBytes);
        var (info, _) = await _files.StartUploadAsync("alice", Start(content));

        var refused = await Assert.ThrowsAsync<FileStoreException>(() => _files.AppendChunkAsync(
            "alice", new FilePutChunkPayload(info.FileId, 12_345, Slice(content, 0))));
        Assert.Equal("BAD_OFFSET", refused.Code);

        var past = await Assert.ThrowsAsync<FileStoreException>(() => _files.AppendChunkAsync(
            "alice", new FilePutChunkPayload(info.FileId, (long)ChunkBytes * 9, Slice(content, 0))));
        Assert.Equal("BAD_OFFSET", past.Code);
    }

    /// <summary>
    /// A manifest that cannot describe this file is refused before a byte is accepted.
    ///
    /// <para>Each of these would otherwise surface much later as an upload that can never be
    /// completed — and the last one, a chunk larger than the store will return in a single read, as
    /// something worse: a file that stores successfully and cannot be read back.</para>
    /// </summary>
    [Fact]
    public async Task AManifestThatCannotDescribeTheFileIsRefused()
    {
        var content = Noise(2 * ChunkBytes);
        var good = Start(content);

        async Task<string> RefusalFor(FilePutStartPayload request) =>
            (await Assert.ThrowsAsync<FileStoreException>(() => _files.StartUploadAsync("alice", request))).Code;

        // One hash too few for the size declared.
        Assert.Equal("BAD_MANIFEST", await RefusalFor(good with { ChunkHashes = [good.ChunkHashes![0]] }));

        // A hash that is not a SHA-256.
        Assert.Equal("BAD_MANIFEST", await RefusalFor(good with { ChunkHashes = [new byte[31], new byte[31]] }));

        // Chunks bigger than the store will hand back in one read.
        Assert.Equal("BAD_MANIFEST", await RefusalFor(good with { ChunkBytes = 8 * 1024 * 1024 }));
    }

    /// <summary>
    /// An upload with no manifest behaves exactly as it always did, and says so when asked to resume.
    ///
    /// <para>This is the compatibility contract in both directions: a client older than the manifest
    /// still uploads here, and this server still refuses to pretend such an upload can be resumed.
    /// Offering a byte count as a resume point would be offering to trust bytes nothing ever
    /// checked.</para>
    /// </summary>
    [Fact]
    public async Task AnUploadWithoutAManifestAppendsAndCannotBeResumed()
    {
        var content = Noise(2 * ChunkBytes);
        var legacy = Start(content) with { ChunkBytes = 0, ChunkHashes = null };
        Assert.False(legacy.HasManifest);

        var (info, _) = await _files.StartUploadAsync("alice", legacy);

        var refused = await Assert.ThrowsAsync<FileStoreException>(
            () => _files.MissingChunksAsync("alice", info.FileId));
        Assert.Equal("NOT_RESUMABLE", refused.Code);

        // And out of order is still refused, which is why it cannot be resumed.
        var outOfOrder = await Assert.ThrowsAsync<FileStoreException>(() => _files.AppendChunkAsync(
            "alice", new FilePutChunkPayload(info.FileId, ChunkBytes, Slice(content, 1))));
        Assert.Equal("BAD_OFFSET", outOfOrder.Code);

        await SendAsync(info.FileId, content, 0);
        await SendAsync(info.FileId, content, 1);
        var (done, _, _) = await _files.FinalizeAsync("alice", info.FileId);
        Assert.Equal(content, await ReadAllAsync(done.FileId));
    }

    /// <summary>
    /// A self-consistent manifest for the wrong content is still caught, by the whole-file hash.
    ///
    /// <para>A manifest can only say that the chunks are the chunks it lists. It cannot say the list
    /// belongs to the file the uploader claimed — so every chunk here verifies perfectly and the
    /// upload must still fail, because the declared SHA-256 is of something else. That is the check
    /// the manifest does not replace, and the reason the final hash is still computed.</para>
    /// </summary>
    [Fact]
    public async Task ChunksThatAllVerifyStillCannotPassForAnotherFile()
    {
        var content = Noise(2 * ChunkBytes);
        var somethingElse = Noise((2 * ChunkBytes) + 1)[..(2 * ChunkBytes)];

        // Honest manifest, honest chunks - and a whole-file hash belonging to other bytes.
        var lying = Start(content) with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(somethingElse)) };
        var (info, _) = await _files.StartUploadAsync("alice", lying);

        await SendAsync(info.FileId, content, 0);
        await SendAsync(info.FileId, content, 1);

        var caught = await Assert.ThrowsAsync<FileStoreException>(() => _files.FinalizeAsync("alice", info.FileId));
        Assert.Equal("HASH_MISMATCH", caught.Code);
    }
}

/// <summary>
/// What happens when the manifest itself would not fit in a frame.
///
/// <para>A manifest costs 32 bytes per chunk, and <c>FILE_PUT_START</c> has to fit one frame like
/// every other message. At 64 KiB chunks against a conduit's 192 KiB ceiling that is a few hundred
/// megabytes of file, and the default per-file cap is 32 MB — so nothing reaches it by default. A
/// deployment that raised the cap would, though, and the failure would be the worst kind: large
/// files becoming <i>unuploadable</i> rather than merely unverified, because the frame carrying
/// their manifest is refused at the transport.</para>
///
/// <para>So the manifest is dropped when it will not fit and the upload is the append-only one it
/// was before manifests existed. Tested rather than assumed, because a fallback nothing exercises
/// is a fallback that stops working quietly.</para>
/// </summary>
public sealed class UploadManifestFitTests : IAsyncLifetime
{
    private readonly NarrowTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore().AddUser("alice", "pw");
    private string _dbPath = null!;
    private string _dataDir = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-fit-{id}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"banter-fit-data-{id}");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
        _server = new BanterServer(
            _transport, _accounts, new DbServerStore(_database),
            new FileStore(_database, new FileStoreOptions { DataDirectory = _dataDir }));
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        File.Delete(_dbPath);
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task AManifestTooBigForAFrameIsDroppedAndTheUploadStillLands()
    {
        await using var alice = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "alice", "pw");
        await alice.JoinAsync("#narrow");

        // Thirty-two chunks, against a connection claiming a 2 KB ceiling: 32 hashes could not ride
        // in that frame, so none should be sent.
        var content = new byte[32 * 64 * 1024];
        Random.Shared.NextBytes(content);

        var info = await alice.UploadFileAsync("#narrow", "wide.bin", content, "application/octet-stream");

        Assert.Equal(content, await alice.DownloadFileAsync(info.FileId));

        // The oracle is the SIZE of the frame that opened the upload: a manifest for 32 chunks is
        // over a kilobyte of hashes, so a small FILE_PUT_START is proof none went.
        Assert.InRange(_transport.LargestStart, 1, 512);
    }

    /// <summary>
    /// A transport that claims a frame ceiling far below what it actually carries, and remembers how
    /// big the upload's opening frame was. Only the claim matters: nothing enforces it on TCP, which
    /// is what lets one test observe a decision the conduit's real ceiling would force.
    /// </summary>
    private sealed class NarrowTransport : IBanterClientTransport, IBanterServerTransport
    {
        private readonly TcpBanterTransport _inner = new();
        private int _largestStart;

        public int LargestStart => Volatile.Read(ref _largestStart);

        public async Task<IBanterConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
            => new Narrow(await _inner.ConnectAsync(endpoint, cancellationToken), this);

        public Task<IBanterListener> ListenAsync(Uri endpoint, CancellationToken cancellationToken = default)
            => _inner.ListenAsync(endpoint, cancellationToken);

        private void Observe(ReadOnlyMemory<byte> frame)
        {
            try
            {
                if (new BanterCodec().DecodeEnvelope(frame).Type == BanterMessageType.FilePutStart)
                {
                    Volatile.Write(ref _largestStart, Math.Max(_largestStart, frame.Length));
                }
            }
            catch (Exception)
            {
                // Not readable as an envelope; nothing to observe.
            }
        }

        private sealed class Narrow(IBanterConnection inner, NarrowTransport owner) : IBanterConnection
        {
            public string RemoteDescription => inner.RemoteDescription;

            public int MaxFrameBytes => 2048;

            public ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
            {
                owner.Observe(frame);
                return inner.SendFrameAsync(frame, cancellationToken);
            }

            public ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default)
                => inner.ReceiveFrameAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
