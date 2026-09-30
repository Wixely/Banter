using System.Collections.Concurrent;
using System.Security.Cryptography;
using Banter.Protocol;
using Banter.Server.Persistence;
using Dapper;

namespace Banter.Server.Files;

public sealed record FileStoreOptions
{
    public required string DataDirectory { get; init; }
    public long MaxFileBytes { get; init; } = 32 * 1024 * 1024;
    public long RoomQuotaBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaxChunkBytes { get; init; } = 256 * 1024;
}

/// <summary>A file operation the client got wrong; <see cref="Code"/> goes into the wire error.</summary>
public sealed class FileStoreException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Room-scoped file storage (PLAN §5a): blobs on disk named by content hash (automatic dedup),
/// metadata + file↔room grants in the database. Uploads are chunked and sequential; in-flight
/// upload state is in-memory, so an interrupted upload restarts from scratch (small files only).
/// </summary>
public sealed class FileStore(BanterDatabase database, FileStoreOptions options) : IAsyncDisposable
{
    /// <summary>
    /// An upload in flight, in one of two shapes (PLAN §5a).
    ///
    /// <para><b>Append-only</b>, when the client sent no manifest: a write-only stream and a running
    /// hash, chunks strictly in order, nothing verified until the end. <b>Verified</b>, when it did:
    /// a read-write stream written at each chunk's own offset, every chunk checked against its hash
    /// as it lands, and a record of which have been accepted — so order stops mattering and an
    /// interrupted upload can be told what it still owes.</para>
    ///
    /// <para>The verified path is <c>ReliquaryDiskAssembler</c>'s job description, and that class is
    /// deliberately not used: it needs an <c>ICryptoSuite</c>, which would put CupriNet.Alembic and a
    /// crypto provider into a project that knows no transport, to compute the SHA-256 that
    /// <see cref="SHA256"/> is already here for. The manifest CONCEPT is shared with the download
    /// side on purpose; the twenty lines that act on it are not worth a dependency.</para>
    /// </summary>
    private sealed class PendingUpload
    {
        public PendingUpload(string uploader, FilePutStartPayload request, string tmpPath)
        {
            Uploader = uploader;
            Request = request;
            TmpPath = tmpPath;

            if (request.HasManifest)
            {
                // Read-write and pre-sized: chunks arrive at their own offsets, in any order, and the
                // whole-file hash is streamed back out of this same handle at the end.
                Stream = new FileStream(tmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                Stream.SetLength(request.Size);
                Received = new bool[request.ChunkHashes!.Count];
            }
            else
            {
                Stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }
        }

        public string Uploader { get; }
        public FilePutStartPayload Request { get; }
        public string TmpPath { get; }
        public FileStream Stream { get; }

        /// <summary>Running hash, append-only path only — a verified upload hashes at the end, out of
        /// the scratch file, because it was not written in order.</summary>
        public IncrementalHash? Hash { get; }

        /// <summary>Which chunks have been accepted, verified path only.</summary>
        public bool[]? Received { get; }

        /// <summary>Bytes accepted. On the append-only path this is also the only offset a chunk may
        /// arrive at; on the verified path it is a count, and says nothing about where.</summary>
        public long BytesWritten { get; set; }

        public bool IsVerified => Received is not null;

        public bool IsComplete => Received is null
            ? BytesWritten == Request.Size
            : Array.TrueForAll(Received, r => r);

        public IReadOnlyList<int> Missing()
        {
            if (Received is null)
            {
                return [];
            }

            var missing = new List<int>();
            for (var i = 0; i < Received.Length; i++)
            {
                if (!Received[i])
                {
                    missing.Add(i);
                }
            }

            return missing;
        }

        /// <summary>How long chunk <paramref name="index"/> must be: the chunk size, except the last
        /// one, which is whatever is left. Hashing a padded buffer would produce a manifest that
        /// never verifies on its final chunk.</summary>
        public int ExpectedLength(int index)
        {
            var offset = (long)index * Request.ChunkBytes;
            return (int)Math.Min(Request.ChunkBytes, Request.Size - offset);
        }
    }

    private readonly ConcurrentDictionary<string, PendingUpload> _pending = new();
    private string BlobsDirectory => Path.Combine(options.DataDirectory, "blobs");
    private string TmpDirectory => Path.Combine(options.DataDirectory, "tmp");

    public int MaxChunkBytes => options.MaxChunkBytes;

    /// <summary>
    /// Closes every upload still in flight. Each one holds an open handle on its <c>.part</c>
    /// file, and an upload interrupted by a dropped connection is never finalised — so without
    /// this, shutting the server down leaves those handles open until the process exits, and the
    /// data directory cannot even be deleted.
    ///
    /// <para>This is shutdown only. An upload abandoned by one disconnect on a server that keeps
    /// running still holds its handle until then: pending uploads are keyed by file id and
    /// attributed to a user rather than to a session, and one user may legitimately be uploading
    /// from two devices at once, so there is nothing here that can safely be dropped when a
    /// single session ends.</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var fileId in _pending.Keys)
        {
            await AbortAsync(fileId).ConfigureAwait(false);
        }
    }

    /// <summary>Validates caps/quota and registers the upload. Returns a complete
    /// <see cref="FileInfoPayload"/> immediately when the content already exists (dedup),
    /// plus the room/quiet flags needed for the announcement.</summary>
    public async Task<(FileInfoPayload Info, bool Quiet)> StartUploadAsync(string uploader, FilePutStartPayload request)
    {
        if (request.Size <= 0)
        {
            throw new FileStoreException("BAD_SIZE", "File size must be positive.");
        }

        if (request.Size > options.MaxFileBytes)
        {
            throw new FileStoreException("FILE_TOO_LARGE", $"File exceeds the {options.MaxFileBytes} byte cap.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new FileStoreException("BAD_NAME", "File name is required.");
        }

        var sha = NormalizeSha(request.Sha256);
        if (request.HasManifest)
        {
            RequireCoherentManifest(request);
        }

        await using var connection = await database.OpenAsync().ConfigureAwait(false);

        var roomBytes = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COALESCE(SUM(f.size), 0) FROM files f
            JOIN file_grants g ON g.file_id = f.file_id
            WHERE g.room = @Room AND f.complete = @Complete
            """,
            new { request.Room, Complete = true }).ConfigureAwait(false);
        if (roomBytes + request.Size > options.RoomQuotaBytes)
        {
            throw new FileStoreException("QUOTA_EXCEEDED", $"{request.Room} would exceed its {options.RoomQuotaBytes} byte quota.");
        }

        var fileId = Guid.NewGuid().ToString("N");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var deduplicated = File.Exists(BlobPath(sha)) && await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM files WHERE sha256 = @Sha AND complete = @Complete)",
            new { Sha = sha, Complete = true }).ConfigureAwait(false);

        await connection.ExecuteAsync(
            """
            INSERT INTO files (file_id, name, mime, size, sha256, uploader, created_at, description, complete)
            VALUES (@FileId, @Name, @Mime, @Size, @Sha, @Uploader, @CreatedAt, @Description, @Complete)
            """,
            new
            {
                FileId = fileId,
                request.Name,
                Mime = request.MimeType,
                request.Size,
                Sha = sha,
                Uploader = uploader,
                CreatedAt = createdAt,
                request.Description,
                Complete = deduplicated,
            }).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "INSERT INTO file_grants (file_id, room) VALUES (@FileId, @Room)",
            new { FileId = fileId, request.Room }).ConfigureAwait(false);

        if (!deduplicated)
        {
            Directory.CreateDirectory(TmpDirectory);
            var pending = new PendingUpload(uploader, request with { Sha256 = sha }, Path.Combine(TmpDirectory, $"{fileId}.part"));
            _pending[fileId] = pending;
        }

        var info = new FileInfoPayload(
            fileId, request.Name, request.MimeType, request.Size, sha, uploader, createdAt,
            request.Description, [request.Room], deduplicated);
        return (info, request.Quiet);
    }

    public async Task AppendChunkAsync(string uploader, FilePutChunkPayload chunk)
    {
        var pending = GetPending(uploader, chunk.FileId);
        if (chunk.Data.Length > options.MaxChunkBytes)
        {
            throw new FileStoreException("CHUNK_TOO_LARGE", $"Chunks are capped at {options.MaxChunkBytes} bytes.");
        }

        if (pending.IsVerified)
        {
            await AcceptVerifiedChunkAsync(pending, chunk).ConfigureAwait(false);
            return;
        }

        if (chunk.Offset != pending.BytesWritten)
        {
            throw new FileStoreException("BAD_OFFSET", $"Expected offset {pending.BytesWritten}, got {chunk.Offset}.");
        }

        if (pending.BytesWritten + chunk.Data.Length > pending.Request.Size)
        {
            await AbortAsync(chunk.FileId).ConfigureAwait(false);
            throw new FileStoreException("TOO_MUCH_DATA", "Upload exceeds its declared size.");
        }

        await pending.Stream.WriteAsync(chunk.Data).ConfigureAwait(false);
        pending.Hash!.AppendData(chunk.Data);
        pending.BytesWritten += chunk.Data.Length;
    }

    /// <summary>
    /// Takes one chunk of a manifested upload: checked against its own hash, written at its own
    /// offset, in any order, and re-sendable.
    ///
    /// <para>The offset names the chunk rather than the append point, which is the whole difference.
    /// It must therefore be chunk-aligned — an offset that is not is a client disagreeing with the
    /// manifest it sent, and guessing which chunk it meant would corrupt the file quietly.</para>
    ///
    /// <para><b>A bad chunk is rejected, not fatal.</b> The append-only path aborts the whole upload
    /// on a surprise because it cannot tell a bad byte from a lost one. Here the manifest says exactly
    /// what was expected, so the only thing wrong is this chunk: the sender can send it again, and
    /// nothing already accepted is lost. That is what makes the difference between per-chunk integrity
    /// and a hash check at the end worth having.</para>
    ///
    /// <para>Re-accepting a chunk already held is deliberately allowed rather than an error. A resume
    /// races the chunks that were in flight when the connection went, and a duplicate that verifies
    /// carries exactly the bytes already on disk.</para>
    /// </summary>
    private static async Task AcceptVerifiedChunkAsync(PendingUpload pending, FilePutChunkPayload chunk)
    {
        var chunkBytes = pending.Request.ChunkBytes;
        if (chunk.Offset < 0 || chunk.Offset % chunkBytes != 0)
        {
            throw new FileStoreException(
                "BAD_OFFSET", $"Offset {chunk.Offset} is not a multiple of the manifest's {chunkBytes}-byte chunk.");
        }

        var index = (int)(chunk.Offset / chunkBytes);
        var received = pending.Received!;
        if (index >= received.Length)
        {
            throw new FileStoreException("BAD_OFFSET", $"Offset {chunk.Offset} is past the end of this upload.");
        }

        var expectedLength = pending.ExpectedLength(index);
        if (chunk.Data.Length != expectedLength)
        {
            throw new FileStoreException(
                "BAD_CHUNK", $"Chunk {index} must be {expectedLength} bytes, not {chunk.Data.Length}.");
        }

        if (!SHA256.HashData(chunk.Data).SequenceEqual(pending.Request.ChunkHashes![index]))
        {
            throw new FileStoreException("BAD_CHUNK", $"Chunk {index} does not match the hash the manifest gave for it.");
        }

        pending.Stream.Position = chunk.Offset;
        await pending.Stream.WriteAsync(chunk.Data).ConfigureAwait(false);

        if (!received[index])
        {
            received[index] = true;
            pending.BytesWritten += chunk.Data.Length;
        }
    }

    /// <summary>
    /// Which chunks of an upload in flight are still owed, so a reconnecting client sends those and
    /// not the file (PLAN §5a).
    ///
    /// <para>Only a manifested upload can answer: without one the server appended blindly and knows a
    /// byte count rather than which chunks are good, and offering that count as a resume point would
    /// be offering to trust bytes nothing ever checked.</para>
    /// </summary>
    public Task<IReadOnlyList<int>> MissingChunksAsync(string uploader, string fileId)
    {
        var pending = GetPending(uploader, fileId);
        if (!pending.IsVerified)
        {
            throw new FileStoreException(
                "NOT_RESUMABLE", "This upload was started without a manifest and cannot be resumed.");
        }

        return Task.FromResult(pending.Missing());
    }

    public async Task<(FileInfoPayload Info, string Room, bool Quiet)> FinalizeAsync(string uploader, string fileId)
    {
        var pending = GetPending(uploader, fileId);

        // Named before the hash is even computed, because the two failures deserve different answers:
        // an upload still missing chunks is unfinished and can be continued, where one whose bytes do
        // not match its hash is wrong and has to start over.
        if (pending.IsVerified && !pending.IsComplete)
        {
            throw new FileStoreException(
                "INCOMPLETE",
                $"{pending.Missing().Count} of {pending.Received!.Length} chunks are still missing; ask FILE_PUT_RESUME which.");
        }

        // A verified upload was written out of order, so there is no running hash to finish - the
        // whole-file check streams back out of the scratch file it just filled.
        var computed = pending.IsVerified
            ? await HashScratchAsync(pending).ConfigureAwait(false)
            : Convert.ToHexStringLower(pending.Hash!.GetHashAndReset());

        await pending.Stream.DisposeAsync().ConfigureAwait(false);

        if (pending.BytesWritten != pending.Request.Size || computed != pending.Request.Sha256)
        {
            await AbortAsync(fileId).ConfigureAwait(false);
            await using var cleanup = await database.OpenAsync().ConfigureAwait(false);
            await cleanup.ExecuteAsync("DELETE FROM file_grants WHERE file_id = @FileId; DELETE FROM files WHERE file_id = @FileId",
                new { FileId = fileId }).ConfigureAwait(false);
            throw new FileStoreException("HASH_MISMATCH", "Uploaded content does not match the declared size/hash.");
        }

        Directory.CreateDirectory(BlobsDirectory);
        var blobPath = BlobPath(computed);
        if (File.Exists(blobPath))
        {
            File.Delete(pending.TmpPath);
        }
        else
        {
            File.Move(pending.TmpPath, blobPath);
        }

        _pending.TryRemove(fileId, out _);

        await using var connection = await database.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync(
            "UPDATE files SET complete = @Complete WHERE file_id = @FileId",
            new { Complete = true, FileId = fileId }).ConfigureAwait(false);

        var info = await GetInfoAsync(fileId).ConfigureAwait(false)
            ?? throw new FileStoreException("NOT_FOUND", "File vanished during finalize.");
        return (info, pending.Request.Room, pending.Request.Quiet);
    }

    public async Task<FileInfoPayload?> GetInfoAsync(string fileId)
    {
        await using var connection = await database.OpenAsync().ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<FileRow>(
            FileSelect + " WHERE f.file_id = @FileId", new { FileId = fileId }).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var rooms = await GetGrantedRoomsAsync(fileId).ConfigureAwait(false);
        return ToInfo(row, rooms);
    }

    public async Task<IReadOnlyList<string>> GetGrantedRoomsAsync(string fileId)
    {
        await using var connection = await database.OpenAsync().ConfigureAwait(false);
        return (await connection.QueryAsync<string>(
            "SELECT room FROM file_grants WHERE file_id = @FileId", new { FileId = fileId }).ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<FileInfoPayload>> ListForRoomAsync(string room)
    {
        await using var connection = await database.OpenAsync().ConfigureAwait(false);
        var rows = (await connection.QueryAsync<FileRow>(
            FileSelect + """

            JOIN file_grants g ON g.file_id = f.file_id
            WHERE g.room = @Room AND f.complete = @Complete
            ORDER BY f.created_at
            """,
            new { Room = room, Complete = true }).ConfigureAwait(false)).AsList();

        var result = new List<FileInfoPayload>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(ToInfo(row, await GetGrantedRoomsAsync(row.FileId).ConfigureAwait(false)));
        }

        return result;
    }

    public async Task<FileChunkPayload> ReadChunkAsync(string fileId, long offset, int maxBytes)
    {
        var info = await GetInfoAsync(fileId).ConfigureAwait(false);
        if (info is null || !info.Complete)
        {
            throw new FileStoreException("NOT_FOUND", "No such file.");
        }

        if (offset < 0 || offset > info.Size)
        {
            throw new FileStoreException("BAD_OFFSET", "Offset is outside the file.");
        }

        var take = (int)Math.Min(Math.Clamp(maxBytes, 1, options.MaxChunkBytes), info.Size - offset);
        var buffer = new byte[take];
        await using var stream = new FileStream(BlobPath(info.Sha256), FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = offset;
        await stream.ReadExactlyAsync(buffer).ConfigureAwait(false);
        return new FileChunkPayload(fileId, offset, buffer, offset + take >= info.Size);
    }

    /// <summary>
    /// Hashes the file a chunk at a time, in order — what a chunked transfer needs to promise a
    /// receiver each chunk before the whole arrives (PLAN §2.5).
    ///
    /// <para>Here because this is the only class that knows where the bytes are. One pass over one
    /// open handle, and the whole-file hash is not recomputed: it is already stored, and a blob
    /// whose content no longer matches its own name is a corrupted store rather than something to
    /// discover on every download.</para>
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> ChunkHashesAsync(
        string fileId,
        int chunkSize,
        CancellationToken cancellationToken = default)
    {
        if (chunkSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        var info = await GetInfoAsync(fileId).ConfigureAwait(false);
        if (info is null || !info.Complete)
        {
            throw new FileStoreException("NOT_FOUND", "No such file.");
        }

        var hashes = new List<byte[]>();
        var buffer = new byte[chunkSize];
        await using var stream = new FileStream(
            BlobPath(info.Sha256), FileMode.Open, FileAccess.Read, FileShare.Read);

        while (true)
        {
            var read = await stream.ReadAtLeastAsync(
                buffer, chunkSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hashes.Add(SHA256.HashData(buffer.AsSpan(0, read)));
            if (read < chunkSize)
            {
                break;
            }
        }

        return hashes;
    }

    public async Task GrantAsync(string requester, string fileId, string room)
    {
        await RequireUploaderAsync(requester, fileId).ConfigureAwait(false);
        await using var connection = await database.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync(
            "INSERT INTO file_grants (file_id, room) VALUES (@FileId, @Room) ON CONFLICT (file_id, room) DO NOTHING",
            new { FileId = fileId, Room = room }).ConfigureAwait(false);
    }

    public async Task RevokeAsync(string requester, string fileId, string room)
    {
        await RequireUploaderAsync(requester, fileId).ConfigureAwait(false);
        await using var connection = await database.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync(
            "DELETE FROM file_grants WHERE file_id = @FileId AND room = @Room",
            new { FileId = fileId, Room = room }).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string requester, string fileId)
    {
        var info = await RequireUploaderAsync(requester, fileId).ConfigureAwait(false);
        await using var connection = await database.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync(
            "DELETE FROM file_grants WHERE file_id = @FileId; DELETE FROM files WHERE file_id = @FileId",
            new { FileId = fileId }).ConfigureAwait(false);

        var stillReferenced = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM files WHERE sha256 = @Sha AND complete = @Complete)",
            new { Sha = info.Sha256, Complete = true }).ConfigureAwait(false);
        if (!stillReferenced && File.Exists(BlobPath(info.Sha256)))
        {
            File.Delete(BlobPath(info.Sha256));
        }
    }

    private async Task<FileInfoPayload> RequireUploaderAsync(string requester, string fileId)
    {
        var info = await GetInfoAsync(fileId).ConfigureAwait(false)
            ?? throw new FileStoreException("NOT_FOUND", "No such file.");
        if (!string.Equals(info.Uploader, requester, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileStoreException("NOT_OWNER", "Only the uploader can manage this file.");
        }

        return info;
    }

    /// <summary>
    /// The SHA-256 of everything accepted, read back out of the scratch file.
    ///
    /// <para>Every chunk was already verified on the way in, so this is not a second opinion about the
    /// chunks — it is the check that they were the chunks of THIS file. A manifest lists hashes; it
    /// cannot say that the list belongs to the content the uploader claimed, and the declared
    /// whole-file hash is what closes that.</para>
    /// </summary>
    private static async Task<string> HashScratchAsync(PendingUpload pending)
    {
        await pending.Stream.FlushAsync().ConfigureAwait(false);
        pending.Stream.Position = 0;
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(pending.Stream).ConfigureAwait(false));
    }

    private PendingUpload GetPending(string uploader, string fileId)
    {
        if (!_pending.TryGetValue(fileId, out var pending))
        {
            throw new FileStoreException("UPLOAD_NOT_FOUND", "No upload in progress for that file id.");
        }

        if (!string.Equals(pending.Uploader, uploader, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileStoreException("NOT_OWNER", "That upload belongs to another user.");
        }

        return pending;
    }

    private async Task AbortAsync(string fileId)
    {
        if (_pending.TryRemove(fileId, out var pending))
        {
            await pending.Stream.DisposeAsync().ConfigureAwait(false);
            if (File.Exists(pending.TmpPath))
            {
                File.Delete(pending.TmpPath);
            }
        }
    }

    /// <summary>
    /// Refuses a manifest that cannot describe this file, before a single byte is accepted.
    ///
    /// <para>All three checks are about the same failure: a manifest the server believes and then
    /// cannot satisfy. A chunk count that disagrees with the size means some chunk has no hash or no
    /// bytes; a chunk larger than the store will hand back in one read means the download side could
    /// never serve what was stored; a hash of the wrong length means the client is not speaking
    /// SHA-256. Each would otherwise surface much later as an upload that cannot be completed, or —
    /// worse — one that completes and cannot be read back.</para>
    /// </summary>
    private void RequireCoherentManifest(FilePutStartPayload request)
    {
        if (request.ChunkBytes < 1 || request.ChunkBytes > options.MaxChunkBytes)
        {
            throw new FileStoreException(
                "BAD_MANIFEST", $"Chunk size must be between 1 and {options.MaxChunkBytes} bytes.");
        }

        var expected = (int)((request.Size + request.ChunkBytes - 1) / request.ChunkBytes);
        var hashes = request.ChunkHashes!;
        if (hashes.Count != expected)
        {
            throw new FileStoreException(
                "BAD_MANIFEST",
                $"A {request.Size}-byte file in {request.ChunkBytes}-byte chunks needs {expected} hashes, not {hashes.Count}.");
        }

        foreach (var hash in hashes)
        {
            if (hash is not { Length: 32 })
            {
                throw new FileStoreException("BAD_MANIFEST", "Every chunk hash must be 32 bytes of SHA-256.");
            }
        }
    }

    private string BlobPath(string sha) => Path.Combine(BlobsDirectory, sha);

    private static string NormalizeSha(string sha)
    {
        if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit))
        {
            throw new FileStoreException("BAD_HASH", "Sha256 must be 64 hex characters.");
        }

        return sha.ToLowerInvariant();
    }

    private const string FileSelect =
        """
        SELECT f.file_id AS FileId, f.name AS Name, f.mime AS Mime, f.size AS Size, f.sha256 AS Sha256,
               f.uploader AS Uploader, f.created_at AS CreatedAt, f.description AS Description, f.complete AS Complete
        FROM files f
        """;

    private sealed class FileRow
    {
        public string FileId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Mime { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public string Uploader { get; set; } = "";
        public long CreatedAt { get; set; }
        public string? Description { get; set; }
        public bool Complete { get; set; }
    }

    private static FileInfoPayload ToInfo(FileRow row, IReadOnlyList<string> rooms) =>
        new(row.FileId, row.Name, row.Mime, row.Size, row.Sha256, row.Uploader, row.CreatedAt,
            row.Description, rooms, row.Complete);
}
