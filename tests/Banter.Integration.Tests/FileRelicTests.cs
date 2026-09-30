using System.Security.Cryptography;
using Banter.Core;
using Banter.Client.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>
/// The minting side of files-as-relics (PLAN §2.5), with no transport in the picture.
///
/// <para>What is being pinned here is a <i>promise</i>. A relic manifest tells a fetcher the length,
/// the chunk size, a hash per chunk and a hash of the whole, and the fetcher then refuses anything
/// that disagrees — so every one of those numbers being right is the difference between a transfer
/// that verifies and a file that cannot be downloaded at all. The end-to-end proof is over a real
/// node in <c>ConduitEndToEndTests</c>; what a running node cannot show is the arithmetic on a final
/// short chunk, an empty file, or a store whose own read ceiling is the smaller of the two.</para>
/// </summary>
public sealed class FileRelicTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private string _dataDir = null!;
    private BanterDatabase _database = null!;

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-relics-{id}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"banter-relics-data-{id}");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        BanterDatabase.ClearSqlitePools();
        File.Delete(_dbPath);
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }

        return Task.CompletedTask;
    }

    private FileStore Store(int maxChunkBytes = 256 * 1024) => new(
        _database,
        new FileStoreOptions
        {
            DataDirectory = _dataDir,
            MaxFileBytes = 8 * 1024 * 1024,
            RoomQuotaBytes = 32 * 1024 * 1024,
            MaxChunkBytes = maxChunkBytes,
        });

    /// <summary>Puts content in the store the way a session would, without needing a session.</summary>
    private static async Task<string> StoreAsync(FileStore files, string name, byte[] content)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var (info, _) = await files.StartUploadAsync(
            "alice", new FilePutStartPayload("#room", name, "application/octet-stream", content.Length, sha, null, true));
        if (info.Complete)
        {
            return info.FileId;
        }

        for (var offset = 0; offset < content.Length; offset += 16 * 1024)
        {
            var take = Math.Min(16 * 1024, content.Length - offset);
            await files.AppendChunkAsync("alice", new FilePutChunkPayload(info.FileId, offset, content[offset..(offset + take)]));
        }

        var (final, _, _) = await files.FinalizeAsync("alice", info.FileId);
        return final.FileId;
    }

    private static byte[] Noise(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Every hash in the manifest is the hash of the bytes a fetcher will be sent for it.
    ///
    /// <para>The length is deliberately not a multiple of the chunk size, because the last chunk is
    /// where this goes wrong: hashing a full buffer when only part of it was read produces a manifest
    /// that is internally consistent, passes every test that only counts chunks, and fails on the
    /// final chunk of every download.</para>
    /// </summary>
    [Fact]
    public async Task EveryChunkHashIsTheHashOfThatChunk()
    {
        var files = Store();
        var content = Noise((192 * 1024 * 3) + 101);
        var fileId = await StoreAsync(files, "noise.bin", content);

        var relics = new FileRelics(files);
        var relic = await relics.MintAsync(fileId);

        Assert.Equal(192 * 1024, relic.ChunkSize);
        Assert.Equal(4, relic.ChunkHashes.Count);
        Assert.Equal(content.Length, relic.Length);
        Assert.Equal(SHA256.HashData(content), relic.FullHash);

        for (var index = 0; index < relic.ChunkHashes.Count; index++)
        {
            var offset = index * relic.ChunkSize;
            var expected = content[offset..Math.Min(offset + relic.ChunkSize, content.Length)];

            var served = await relics.ReadChunkAsync(relic.Name, index);
            Assert.NotNull(served);
            Assert.Equal(expected, served);
            Assert.Equal(SHA256.HashData(expected), relic.ChunkHashes[index]);
        }

        // And the last one really was the short one, rather than a full chunk read past the end.
        var tail = await relics.ReadChunkAsync(relic.Name, relic.ChunkHashes.Count - 1);
        Assert.Equal(101, tail!.Length);
    }

    /// <summary>
    /// The chunk count follows from the length, and a boundary is where that is wrong or right.
    ///
    /// <para>No empty case, and that is a fact about the store rather than an omission: it refuses a
    /// zero-length upload outright, so no relic ever describes one and the zero-chunk arithmetic is
    /// unreachable. A single byte is the smallest a manifest has to account for.</para>
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(192 * 1024, 1)]
    [InlineData((192 * 1024) + 1, 2)]
    public async Task ChunkCountsFollowTheLength(int length, int expectedChunks)
    {
        var files = Store();
        var content = Noise(length);
        var fileId = await StoreAsync(files, $"size-{length}.bin", content);

        var relic = await new FileRelics(files).MintAsync(fileId);

        Assert.Equal(expectedChunks, relic.ChunkHashes.Count);
        Assert.Equal(length, relic.Length);
        Assert.Equal(SHA256.HashData(content), relic.FullHash);
    }

    /// <summary>
    /// The chunk size never exceeds what the store will hand over in one read.
    ///
    /// <para>Two independent limits meet here, and they are configured in different places: the relic
    /// chunk size, which is the rite's frame ceiling, and <c>FileStoreOptions.MaxChunkBytes</c>, which
    /// is how much a read will return. If the manifest promised the larger, every read would come
    /// back clamped and short, every chunk hash would fail, and a deployment that had only changed a
    /// storage setting would present as every file being corrupt. So the smaller wins, and the
    /// manifest is built around it.</para>
    /// </summary>
    [Fact]
    public async Task AStoreWithASmallerReadCeilingGetsSmallerChunks()
    {
        var files = Store(maxChunkBytes: 64 * 1024);
        var content = Noise(200_000);
        var fileId = await StoreAsync(files, "clamped.bin", content);

        var relics = new FileRelics(files);
        var relic = await relics.MintAsync(fileId);

        Assert.Equal(64 * 1024, relic.ChunkSize);
        Assert.Equal(4, relic.ChunkHashes.Count);

        // The point of the clamp: what is served matches what was promised.
        for (var index = 0; index < relic.ChunkHashes.Count; index++)
        {
            var served = await relics.ReadChunkAsync(relic.Name, index);
            Assert.Equal(relic.ChunkHashes[index], SHA256.HashData(served!));
        }
    }

    /// <summary>
    /// A name stops working, and nothing has to sweep it for that to be true.
    ///
    /// <para>This is the bound on the whole design: the name is a bearer capability that the rite
    /// serving it cannot attribute to an account, so the only thing standing between a revoked grant
    /// and a completed download is how long the name lasts. Expiry is therefore checked on the way
    /// out, not on a timer — a source that answered until a sweep ran would be a source that answers
    /// for however long the sweep is late.</para>
    /// </summary>
    [Fact]
    public async Task AnExpiredNameResolvesToNothing()
    {
        var files = Store();
        var fileId = await StoreAsync(files, "brief.bin", Noise(1_000));

        var relics = new FileRelics(files, new FileRelicOptions { Lifetime = TimeSpan.FromMilliseconds(60) });
        var relic = await relics.MintAsync(fileId);
        Assert.NotNull(relics.Find(relic.Name));
        Assert.NotNull(await relics.ReadChunkAsync(relic.Name, 0));

        await Task.Delay(120);

        Assert.Null(relics.Find(relic.Name));
        Assert.Null(await relics.ReadChunkAsync(relic.Name, 0));
    }

    /// <summary>
    /// A name nobody minted, and an index past the end of one that was, both resolve to nothing —
    /// which is all a guess can ever learn.
    /// </summary>
    [Fact]
    public async Task NothingIsServedForANameOrAnIndexThatWasNeverMinted()
    {
        var files = Store();
        var fileId = await StoreAsync(files, "bounded.bin", Noise(1_000));

        var relics = new FileRelics(files);
        var relic = await relics.MintAsync(fileId);

        Assert.Null(relics.Find("nobody/minted-this"));
        Assert.Null(await relics.ReadChunkAsync("nobody/minted-this", 0));
        Assert.Null(await relics.ReadChunkAsync(relic.Name, 1));
        Assert.Null(await relics.ReadChunkAsync(relic.Name, -1));
    }

    /// <summary>
    /// A ticket is handed back while it has life in it, and replaced once it has not.
    ///
    /// <para>Reuse is what stops a room of people opening one attachment from hashing it once each.
    /// The half-life rule is the part worth pinning: handing back a name with a second left on it
    /// would start transfers that cannot finish, which is worse than minting.</para>
    /// </summary>
    [Fact]
    public async Task ATicketIsReusedUntilItIsTooCloseToExpiring()
    {
        var files = Store();
        var fileId = await StoreAsync(files, "reused.bin", Noise(1_000));

        var relics = new FileRelics(files, new FileRelicOptions { Lifetime = TimeSpan.FromMilliseconds(400) });
        var first = await relics.MintAsync(fileId);
        Assert.Equal(first.Name, (await relics.MintAsync(fileId)).Name);

        // Past half-life: still valid, no longer worth handing out.
        await Task.Delay(250);
        var second = await relics.MintAsync(fileId);
        Assert.NotEqual(first.Name, second.Name);
    }

    /// <summary>
    /// Minting is refused rather than evicting a live name.
    ///
    /// <para>Refusing is the honest answer because of what the caller does with it: <c>RELIC_BUSY</c>
    /// sends the download over the frame pipe, which still works. Evicting someone else's live ticket
    /// to make room would instead break a transfer already running, to start one that might not.</para>
    /// </summary>
    [Fact]
    public async Task MintingIsRefusedRatherThanEvictingALiveTicket()
    {
        var files = Store();
        var first = await StoreAsync(files, "one.bin", Noise(1_000));
        var second = await StoreAsync(files, "two.bin", Noise(2_000));

        var relics = new FileRelics(files, new FileRelicOptions { MaxLiveTickets = 1 });
        var held = await relics.MintAsync(first);

        var refused = await Assert.ThrowsAsync<FileStoreException>(() => relics.MintAsync(second));
        Assert.Equal("RELIC_BUSY", refused.Code);

        // The one already handed out is untouched.
        Assert.NotNull(relics.Find(held.Name));
    }

    /// <summary>
    /// Forgetting a file's names is deliberate, not a side effect of the blob going away.
    ///
    /// <para>Deletion is half-covered by accident — once the blob is gone the read fails on its own —
    /// which is exactly why this is worth a test of its own: revoke leaves the bytes untouched, so
    /// nothing fails by accident and a name that was not forgotten keeps working perfectly.</para>
    /// </summary>
    [Fact]
    public async Task ForgettingAFileDropsEveryNameMintedForIt()
    {
        var files = Store();
        var kept = await StoreAsync(files, "kept.bin", Noise(500));
        var dropped = await StoreAsync(files, "dropped.bin", Noise(600));

        var relics = new FileRelics(files);
        var keptRelic = await relics.MintAsync(kept);
        var droppedRelic = await relics.MintAsync(dropped);

        relics.Forget(dropped);

        Assert.Null(relics.Find(droppedRelic.Name));
        Assert.Null(await relics.ReadChunkAsync(droppedRelic.Name, 0));

        // And only that file's. Forgetting one attachment must not cancel everyone else's transfers.
        Assert.NotNull(relics.Find(keptRelic.Name));
        Assert.NotNull(await relics.ReadChunkAsync(keptRelic.Name, 0));
    }

    /// <summary>
    /// A file name that could never be part of a relic name still mints.
    ///
    /// <para>Relic names are guarded as relative paths by the rite, and an uploaded file name is a
    /// user's string — traversal, separators, or nothing usable at all. Cutting it down here is the
    /// difference between a peculiar name and a file that cannot be downloaded.</para>
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd", "etcpasswd")]
    [InlineData("..", "file")]
    // Nothing ASCII but the extension: what survives must not begin with the dot it was separated
    // by, or an ordinary upload becomes a hidden file.
    [InlineData("\u4f60\u597d.txt", "txt")]
    [InlineData("holiday photo (1).JPG", "holidayphoto1.JPG")]
    public async Task AnUnusableFileNameIsCutDownRatherThanRefused(string uploaded, string expected)
    {
        var files = Store();
        var fileId = await StoreAsync(files, uploaded, Noise(64));

        var relic = await new FileRelics(files).MintAsync(fileId);

        Assert.Equal(expected, relic.Path);
        Assert.EndsWith($"/{expected}", relic.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("..", relic.Name, StringComparison.Ordinal);
    }
}

/// <summary>
/// A client whose transport offers the Relic rite, against a server that does not answer for it —
/// the mixed-version case, and the one where a silent fallback has to work rather than be assumed.
///
/// <para>Two codes reach here in practice and they are not the same event: <c>NO_RELICS</c> from a
/// current server built without a relic source, and <c>UNSUPPORTED</c> from one predating
/// <c>FILE_RELIC</c>, which is what any server answers for a message type it has no contract for.
/// This fixture produces the first. Both are handled by the same clause for the same reason — the
/// bytes are still reachable over <c>FILE_GET</c> — and treating either as a failure would mean a
/// client could not download from a server one release behind it.</para>
///
/// <para>The transport is a decorator rather than a mesh, because what is under test is the client's
/// decision. TCP is the only transport that can be stood up in-process, and the thing that makes
/// <c>DownloadFileAsync</c> try a relic at all is the connection implementing
/// <see cref="IBanterRelicFetch"/> — so lending that to a TCP connection reproduces the case exactly,
/// without a node.</para>
/// </summary>
public sealed class RelicFallbackTests : IAsyncLifetime
{
    private readonly RelicCapableTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore().AddUser("alice", "pw");
    private string _dbPath = null!;
    private string _dataDir = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-fallback-{id}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"banter-fallback-data-{id}");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();

        // Deliberately no relics: this is the server the fallback exists for.
        _server = new BanterServer(
            _transport,
            _accounts,
            new DbServerStore(_database),
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
    public async Task AServerThatServesNoRelicsStillHandsOverTheFile()
    {
        await using var alice = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "alice", "pw");
        await alice.JoinAsync("#files");

        var raised = new List<Exception>();
        alice.RelicFetchFailed += (_, exception) => raised.Add(exception);

        var content = new byte[100_000];
        new Random(7).NextBytes(content);
        var stored = await alice.UploadFileAsync("#files", "plain.bin", content, "application/octet-stream");

        // The rite is on offer, so the download asks for a ticket and is refused by name.
        Assert.NotNull(alice.RelicFetch);
        var refused = await Assert.ThrowsAsync<BanterErrorException>(() => alice.RequestRelicAsync(stored.FileId));
        Assert.Equal("NO_RELICS", refused.Code);

        Assert.Equal(content, await alice.DownloadFileAsync(stored.FileId));

        // And it was not reported as a fault. RelicFetchFailed is for a rite that broke mid-transfer;
        // a server that never offered one is not a failure to announce.
        Assert.Empty(raised);
    }

    /// <summary>
    /// A TCP transport lending its connections the relic rite — and a fetch that must never be
    /// reached, because the ticket is refused first. Throwing rather than returning empty is the
    /// point: if the client ever fetched without a ticket, that is a defect this hides by faking.
    /// </summary>
    private sealed class RelicCapableTransport : IBanterClientTransport, IBanterServerTransport
    {
        private readonly TcpBanterTransport _inner = new();

        public async Task<IBanterConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
            => new Lent(await _inner.ConnectAsync(endpoint, cancellationToken));

        public Task<IBanterListener> ListenAsync(Uri endpoint, CancellationToken cancellationToken = default)
            => _inner.ListenAsync(endpoint, cancellationToken);

        private sealed class Lent(IBanterConnection inner) : IBanterConnection, IBanterRelicFetch
        {
            public string RemoteDescription => inner.RemoteDescription;

            public int MaxFrameBytes => inner.MaxFrameBytes;

            public ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
                => inner.SendFrameAsync(frame, cancellationToken);

            public ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default)
                => inner.ReceiveFrameAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();

            public Task<byte[]> FetchRelicAsync(
                string relicName,
                long maxBytes,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException(
                    $"Fetched '{relicName}' without a ticket — the refusal should have come first.");
        }
    }
}
