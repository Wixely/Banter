using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>Room-scoped storage (PLAN §5a) end to end: upload/announce/list/download, dedup,
/// access control, caps and quotas, revoke/delete.</summary>
public sealed class FileStorageTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("alice", "pw-a")
        .AddUser("bob", "pw-b")
        .AddUser("mallory", "pw-m");
    private string _dbPath = null!;
    private string _dataDir = null!;
    private BanterDatabase _database = null!;
    private FileStore _files = null!;
    private BanterServer _server = null!;

    // Tiny caps so limit tests stay fast: 50 KB per file, 120 KB per room.
    private const long MaxFileBytes = 50_000;
    private const long RoomQuotaBytes = 120_000;

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-files-{id}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"banter-files-data-{id}");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
        _files = new FileStore(_database, new FileStoreOptions
        {
            DataDirectory = _dataDir,
            MaxFileBytes = MaxFileBytes,
            RoomQuotaBytes = RoomQuotaBytes,
        });
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), _files);
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

    private Task<BanterClient> ConnectAsync(string user, string secret) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, secret);

    private static byte[] Content(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed + i);
        }

        return bytes;
    }

    [Fact]
    public async Task UploadAnnouncesListsAndDownloads()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await using var bob = await ConnectAsync("bob", "pw-b");
        await alice.JoinAsync("#share");
        await bob.JoinAsync("#share");

        var announcement = new TaskCompletionSource<MsgPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.MessageReceived += m => announcement.TrySetResult(m);

        var content = Content(150_00, seed: 1); // 15 KB, spans multiple 64 KB-capped chunks? single chunk; fine
        var info = await alice.UploadFileAsync("#share", "notes.txt", content, "text/plain", "meeting notes");

        Assert.True(info.Complete);
        Assert.Equal(content.Length, info.Size);
        Assert.Equal("alice", info.Uploader);
        Assert.Contains("#share", info.Rooms);

        // The room saw an announcement message carrying the file reference.
        var msg = await announcement.Task.WaitAsync(Timeout);
        Assert.Equal(info.FileId, msg.FileId);
        Assert.Equal("notes.txt", msg.Text);
        Assert.Equal("alice", msg.Sender);

        // Listed for members, and downloadable byte-for-byte by another member.
        var list = await bob.ListFilesAsync("#share");
        var listed = Assert.Single(list.Files);
        Assert.Equal(info.FileId, listed.FileId);
        Assert.Equal(content, await bob.DownloadFileAsync(info.FileId));

        // Metadata fetch works too.
        var fetched = await bob.GetFileInfoAsync(info.FileId);
        Assert.Equal("meeting notes", fetched.Description);
    }

    [Fact]
    public async Task LargeUploadSpansManyChunks()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await alice.JoinAsync("#big");

        var content = Content(49_999, seed: 7); // just under the cap; not a multiple of 64 KB chunks
        var info = await alice.UploadFileAsync("#big", "blob.bin", content, "application/octet-stream", quiet: true);
        Assert.Equal(content, await alice.DownloadFileAsync(info.FileId));
    }

    [Fact]
    public async Task QuietUploadEmitsNoAnnouncement()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await alice.JoinAsync("#quiet");

        var first = new TaskCompletionSource<MsgPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.MessageReceived += m => first.TrySetResult(m);

        await alice.UploadFileAsync("#quiet", "silent.txt", Content(64, 2), "text/plain", quiet: true);
        await alice.SendMessageAsync("#quiet", "marker");

        // Ordering through the single-writer engine: if the quiet upload had announced, that
        // message would arrive before the marker.
        var msg = await first.Task.WaitAsync(Timeout);
        Assert.Equal("marker", msg.Text);
        Assert.Null(msg.FileId);
    }

    [Fact]
    public async Task NonMembersCannotSeeOrFetchFiles()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await using var mallory = await ConnectAsync("mallory", "pw-m");
        await alice.JoinAsync("#private");
        var info = await alice.UploadFileAsync("#private", "secret.txt", Content(64, 3), "text/plain", quiet: true);

        var listEx = await Assert.ThrowsAsync<BanterErrorException>(() => mallory.ListFilesAsync("#private"));
        Assert.Equal("NOT_IN_ROOM", listEx.Code);

        var getEx = await Assert.ThrowsAsync<BanterErrorException>(() => mallory.DownloadFileAsync(info.FileId));
        Assert.Equal("NO_ACCESS", getEx.Code);

        var infoEx = await Assert.ThrowsAsync<BanterErrorException>(() => mallory.GetFileInfoAsync(info.FileId));
        Assert.Equal("NO_ACCESS", infoEx.Code);
    }

    [Fact]
    public async Task IdenticalContentDeduplicatesAtStart()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await alice.JoinAsync("#dedup");
        var content = Content(1_000, 4);

        var original = await alice.UploadFileAsync("#dedup", "one.bin", content, "application/octet-stream", quiet: true);
        var copy = await alice.UploadFileAsync("#dedup", "two.bin", content, "application/octet-stream", quiet: true);

        Assert.NotEqual(original.FileId, copy.FileId);
        Assert.Equal(original.Sha256, copy.Sha256);
        Assert.True(copy.Complete); // no chunks were sent — UploadFileAsync returned at start
        Assert.Equal(content, await alice.DownloadFileAsync(copy.FileId));
    }

    [Fact]
    public async Task CapsAndQuotasAreEnforced()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await alice.JoinAsync("#limits");

        var tooBig = await Assert.ThrowsAsync<BanterErrorException>(() =>
            alice.UploadFileAsync("#limits", "big.bin", Content(60_000, 5), "application/octet-stream", quiet: true));
        Assert.Equal("FILE_TOO_LARGE", tooBig.Code);

        // Distinct content each time so dedup can't dodge the quota.
        await alice.UploadFileAsync("#limits", "a.bin", Content(45_000, 6), "application/octet-stream", quiet: true);
        await alice.UploadFileAsync("#limits", "b.bin", Content(45_000, 7), "application/octet-stream", quiet: true);
        var overQuota = await Assert.ThrowsAsync<BanterErrorException>(() =>
            alice.UploadFileAsync("#limits", "c.bin", Content(45_000, 8), "application/octet-stream", quiet: true));
        Assert.Equal("QUOTA_EXCEEDED", overQuota.Code);
    }

    [Fact]
    public async Task GrantRevokeAndDeleteControlTheLifecycle()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await using var bob = await ConnectAsync("bob", "pw-b");
        await alice.JoinAsync("#origin");
        await bob.JoinAsync("#annex");

        var info = await alice.UploadFileAsync("#origin", "shared.txt", Content(128, 9), "text/plain", quiet: true);

        // Bob can't reach it until alice grants his room.
        await Assert.ThrowsAsync<BanterErrorException>(() => bob.DownloadFileAsync(info.FileId));
        await alice.GrantFileAsync(info.FileId, "#annex");
        Assert.NotEmpty(await bob.DownloadFileAsync(info.FileId));

        // Only the uploader manages grants.
        var notOwner = await Assert.ThrowsAsync<BanterErrorException>(() => bob.RevokeFileAsync(info.FileId, "#annex"));
        Assert.Equal("NOT_OWNER", notOwner.Code);

        await alice.RevokeFileAsync(info.FileId, "#annex");
        var revoked = await Assert.ThrowsAsync<BanterErrorException>(() => bob.DownloadFileAsync(info.FileId));
        Assert.Equal("NO_ACCESS", revoked.Code);

        await alice.DeleteFileAsync(info.FileId);
        var deleted = await Assert.ThrowsAsync<BanterErrorException>(() => alice.GetFileInfoAsync(info.FileId));
        Assert.Equal("NO_ACCESS", deleted.Code); // gone: no grants left to intersect
    }

    [Fact]
    public async Task HashMismatchIsRejectedAtFinalize()
    {
        // Straight at the store: declare one hash, upload different bytes.
        var declared = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Content(64, 10)));
        var (info, _) = await _files.StartUploadAsync("alice",
            new FilePutStartPayload("#x", "lie.bin", "application/octet-stream", 64, declared, null, Quiet: true));
        await _files.AppendChunkAsync("alice", new FilePutChunkPayload(info.FileId, 0, Content(64, 11)));

        var ex = await Assert.ThrowsAsync<FileStoreException>(() => _files.FinalizeAsync("alice", info.FileId));
        Assert.Equal("HASH_MISMATCH", ex.Code);
        Assert.Null(await _files.GetInfoAsync(info.FileId)); // metadata cleaned up
    }
}
