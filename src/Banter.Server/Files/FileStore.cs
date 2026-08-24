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
public sealed class FileStore(BanterDatabase database, FileStoreOptions options)
{
    private sealed class PendingUpload(string uploader, FilePutStartPayload request, string tmpPath)
    {
        public string Uploader { get; } = uploader;
        public FilePutStartPayload Request { get; } = request;
        public string TmpPath { get; } = tmpPath;
        public FileStream Stream { get; } = new(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long BytesWritten { get; set; }
    }

    private readonly ConcurrentDictionary<string, PendingUpload> _pending = new();
    private string BlobsDirectory => Path.Combine(options.DataDirectory, "blobs");
    private string TmpDirectory => Path.Combine(options.DataDirectory, "tmp");

    public int MaxChunkBytes => options.MaxChunkBytes;

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
        pending.Hash.AppendData(chunk.Data);
        pending.BytesWritten += chunk.Data.Length;
    }

    public async Task<(FileInfoPayload Info, string Room, bool Quiet)> FinalizeAsync(string uploader, string fileId)
    {
        var pending = GetPending(uploader, fileId);
        await pending.Stream.DisposeAsync().ConfigureAwait(false);

        var computed = Convert.ToHexStringLower(pending.Hash.GetHashAndReset());
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
