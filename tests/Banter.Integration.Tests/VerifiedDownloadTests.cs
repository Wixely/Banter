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
/// Describing a stored file so a download can check it (PLAN §5a).
///
/// <para>A relic gives this on the mesh. These cover the other half — the same guarantee over the
/// frame pipe, which is what the CLI and any non-mesh deployment actually use. What is pinned here is
/// the server's side of it: the chunk size is the server's choice, and the two limits that bound that
/// choice pull in opposite directions.</para>
/// </summary>
public sealed class VerifiedDownloadTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private string _dataDir = null!;
    private BanterDatabase _database = null!;

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-vdl-{id}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"banter-vdl-data-{id}");
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
            MaxFileBytes = 64 * 1024 * 1024,
            RoomQuotaBytes = 256 * 1024 * 1024,
            MaxChunkBytes = maxChunkBytes,
        });

    private static async Task<string> StoreAsync(FileStore files, string name, byte[] content)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var (info, _) = await files.StartUploadAsync(
            "alice", new FilePutStartPayload("#room", name, "application/octet-stream", content.Length, sha, null, true));
        for (var offset = 0; offset < content.Length; offset += 64 * 1024)
        {
            var take = Math.Min(64 * 1024, content.Length - offset);
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
    /// The description matches the bytes, chunk for chunk, including the short last one.
    /// </summary>
    [Fact]
    public async Task TheManifestDescribesTheFileItIsFor()
    {
        var files = Store();
        var content = Noise((3 * 64 * 1024) + 37);
        var fileId = await StoreAsync(files, "described.bin", content);

        var (size, chunkBytes, hashes) = await files.DescribeForDownloadAsync(fileId, 192 * 1024);

        Assert.Equal(content.Length, size);
        Assert.Equal(64 * 1024, chunkBytes);
        Assert.Equal(4, hashes.Count);

        for (var index = 0; index < hashes.Count; index++)
        {
            var offset = index * chunkBytes;
            var slice = content[offset..Math.Min(offset + chunkBytes, content.Length)];
            Assert.Equal(SHA256.HashData(slice), hashes[index]);
        }

        // The last chunk is the short one, not a padded full chunk.
        Assert.Equal(SHA256.HashData(content[(3 * 64 * 1024)..]), hashes[3]);
    }

    /// <summary>
    /// A narrow frame gets bigger chunks, because fewer of them means fewer hashes.
    ///
    /// <para>This is the whole reason the server chooses: a chunk has to be returnable in one
    /// FILE_GET, so it cannot exceed the store's read ceiling, and the manifest has to fit one frame,
    /// so the hashes cannot exceed that. A caller naming its own chunk size could satisfy neither.</para>
    /// </summary>
    [Fact]
    public async Task ANarrowFrameGetsBiggerChunksSoTheManifestFits()
    {
        var files = Store();
        var content = Noise(4 * 1024 * 1024);
        var fileId = await StoreAsync(files, "wide.bin", content);

        // Roomy: the default chunking's 64 hashes are nothing against a 192 KiB frame.
        var (_, roomy, roomyHashes) = await files.DescribeForDownloadAsync(fileId, 192 * 1024);
        Assert.Equal(64 * 1024, roomy);
        Assert.Equal(64, roomyHashes.Count);

        // Tight: 64 hashes is 2048 bytes against a budget of half of 2048, so it has to coarsen —
        // to 128 KiB, where 32 hashes is exactly the 1024 bytes that will fit.
        var (_, tight, tightHashes) = await files.DescribeForDownloadAsync(fileId, 2048);
        Assert.Equal(128 * 1024, tight);
        Assert.True(tight > roomy, $"chunk size stayed at {tight} for a 2048-byte frame");
        Assert.True(tightHashes.Count * 32 <= 1024, $"{tightHashes.Count} hashes will not fit the budget");

        // Still a description of the same file, at the coarser chunking.
        for (var index = 0; index < tightHashes.Count; index++)
        {
            var offset = index * tight;
            Assert.Equal(SHA256.HashData(content[offset..Math.Min(offset + tight, content.Length)]), tightHashes[index]);
        }
    }

    /// <summary>
    /// A file too big to describe inside the frame it would have to travel in is refused by name, so
    /// the caller can fall back rather than guess.
    /// </summary>
    [Fact]
    public async Task AFileThatCannotBeDescribedInOneFrameIsRefusedByName()
    {
        // A 64 KiB read ceiling means chunks can never coarsen past that, so a large file needs more
        // hashes than a small frame can hold.
        var files = Store(maxChunkBytes: 64 * 1024);
        var content = Noise(4 * 1024 * 1024);
        var fileId = await StoreAsync(files, "undescribable.bin", content);

        var refused = await Assert.ThrowsAsync<FileStoreException>(
            () => files.DescribeForDownloadAsync(fileId, 2048));
        Assert.Equal("NO_MANIFEST", refused.Code);

        // And the same file is perfectly describable over a roomier path.
        var (_, _, hashes) = await files.DescribeForDownloadAsync(fileId, 192 * 1024);
        Assert.Equal(64, hashes.Count);
    }
}

/// <summary>
/// A download over the frame pipe, checked against the server's own description of the file.
///
/// <para>Before this the loop verified nothing at all: whatever arrived became the file, so a
/// truncated or corrupted download was indistinguishable from a short one. The mesh had the guarantee
/// through relics; this is every other transport getting it.</para>
/// </summary>
public sealed class FrameDownloadVerificationTests : IAsyncLifetime
{
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore().AddUser("alice", "pw");
    private string _dbPath = null!;
    private string _dataDir = null!;
    private BanterDatabase _database = null!;
    private FileStore _files = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-rot-{id}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"banter-rot-data-{id}");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
        _files = new FileStore(_database, new FileStoreOptions { DataDirectory = _dataDir });
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), _files);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _files.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        File.Delete(_dbPath);
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    /// <summary>
    /// Damage to the stored file is caught rather than served.
    ///
    /// <para>The manifest is published first and the blob is damaged afterwards, which is exactly the
    /// shape of the failure worth catching: bit rot, a bad disk, or a botched restore, on content the
    /// server has already told someone the hashes of. Same length, one byte different — the case no
    /// size check can see and the old loop would have handed over without a word.</para>
    ///
    /// <para>It fails rather than retrying, because the server is serving what it has: asking again
    /// returns the same wrong bytes.</para>
    /// </summary>
    [Fact]
    public async Task DamageToTheStoredFileIsCaughtRatherThanServed()
    {
        await using var alice = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "alice", "pw");
        await alice.JoinAsync("#rot");

        var content = new byte[3 * 64 * 1024];
        Random.Shared.NextBytes(content);
        var info = await alice.UploadFileAsync("#rot", "rot.bin", content, "application/octet-stream");

        // Intact first: the same call that a reader makes, and the manifest it publishes.
        Assert.Equal(content, await alice.DownloadFileAsync(info.FileId));

        // Now rot one byte of the blob, which is named by the content hash it no longer has.
        var blob = Path.Combine(_dataDir, "blobs", info.Sha256);
        var stored = await File.ReadAllBytesAsync(blob);
        stored[100_000] ^= 0xFF;
        await File.WriteAllBytesAsync(blob, stored);

        var caught = await Assert.ThrowsAsync<BanterClientException>(() => alice.DownloadFileAsync(info.FileId));
        Assert.Contains("does not match the manifest", caught.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bytes that come back are the bytes that went up, chunk boundaries and all.
    ///
    /// <para>A length deliberately not a multiple of the chunk size: the last chunk is short, and a
    /// loop that assembled at the wrong offsets or padded the tail would produce a file of the right
    /// size and the wrong content.</para>
    /// </summary>
    [Fact]
    public async Task AVerifiedDownloadReassemblesExactly()
    {
        await using var alice = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "alice", "pw");
        await alice.JoinAsync("#exact");

        var content = new byte[(5 * 64 * 1024) + 1];
        Random.Shared.NextBytes(content);
        var info = await alice.UploadFileAsync("#exact", "exact.bin", content, "application/octet-stream");

        Assert.Equal(content, await alice.DownloadFileAsync(info.FileId));

        // And a single-byte file, where the only chunk is also the last one.
        var tiny = new byte[] { 42 };
        var tinyInfo = await alice.UploadFileAsync("#exact", "tiny.bin", tiny, "application/octet-stream");
        Assert.Equal(tiny, await alice.DownloadFileAsync(tinyInfo.FileId));
    }
}
