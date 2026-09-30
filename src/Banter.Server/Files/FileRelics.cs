using System.Collections.Concurrent;
using System.Security.Cryptography;
using Banter.Protocol.Transport;

namespace Banter.Server.Files;

public sealed record FileRelicOptions
{
    /// <summary>
    /// How much of a file travels in one chunk. The default is CupriNet's own
    /// <c>RelicCodec.DefaultChunkBytes</c>, written out rather than referenced because this
    /// assembly deliberately knows no transport — the number is the WebRTC DataChannel's practical
    /// message ceiling, which is why the rite chose it.
    /// </summary>
    public int ChunkBytes { get; init; } = 192 * 1024;

    /// <summary>
    /// How long a minted name keeps working. Short on purpose: the name is a bearer capability that
    /// the rite serving it cannot attribute to an account, so this window is exactly how long a
    /// revoked grant or a departed member stays able to finish a download. Long enough to transfer
    /// the largest file a store accepts over a slow link; not long enough to be a share link.
    /// </summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A ward on the tickets held at once. Each carries its file's chunk hashes — 32 bytes per
    /// chunk, so a few KB for a large file — and they are only ever added by an authenticated
    /// request; "bounded" is still cheaper than reasoning about it later.
    /// </summary>
    public int MaxLiveTickets { get; init; } = 4096;
}

/// <summary>
/// Mints the unguessable names that let a file be fetched as a relic, and answers for them
/// afterwards (PLAN §2.5).
///
/// <para><b>Why this exists at all.</b> The Relic rite is served on the same authenticated visit as
/// the conduit, but its source is asked for a <i>name</i> and told nothing about who is asking.
/// Banter's files are visible through room membership, which is a fact about a session. Naming
/// relics after file ids would hand every room's files to anyone who completes a handshake — so the
/// check stays on the conduit, where the session is, and its result becomes a name that is hard to
/// guess and does not last. <c>ClientSession</c> mints only after the same access check that guards
/// <c>FILE_GET</c>.</para>
///
/// <para>A live ticket for a file is reused rather than reminted while more than half its life is
/// left, which is what keeps a room full of people opening the same attachment from hashing it once
/// each. The half is not arbitrary: handing back a ticket about to expire would start a transfer
/// that cannot finish.</para>
/// </summary>
public sealed class FileRelics(FileStore files, FileRelicOptions? options = null) : IBanterRelicSource
{
    private readonly FileRelicOptions _options = options ?? new FileRelicOptions();
    private readonly ConcurrentDictionary<string, BanterRelic> _tickets = new(StringComparer.Ordinal);

    /// <summary>
    /// Names a relic for a file the caller has already been shown to be allowed.
    ///
    /// <para>The name is 256 bits of randomness and the file's own name: the randomness is the
    /// authority, and the suffix is so that a transfer is legible in a log and on the receiving
    /// side. The suffix is sanitised rather than trusted — the rite guards relic names as relative
    /// paths, and an uploaded file name is a user's string.</para>
    /// </summary>
    public async Task<BanterRelic> MintAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        Prune(now);

        if (Reusable(fileId, now) is { } existing)
        {
            return existing;
        }

        var info = await files.GetInfoAsync(fileId).ConfigureAwait(false);
        if (info is null || !info.Complete)
        {
            throw new FileStoreException("NOT_FOUND", "No such file.");
        }

        if (_tickets.Count >= _options.MaxLiveTickets)
        {
            // Full of tickets that have not expired yet. Refusing is the honest answer: the caller
            // falls back to the frame pipe, which still works, rather than being handed a name that
            // was evicted from under it.
            throw new FileStoreException("RELIC_BUSY", "Too many transfers in flight; try again shortly.");
        }

        // The store's own read ceiling wins where it is the smaller: a chunk it would truncate is a
        // chunk whose hash can never match, which would present a configuration mistake as a
        // corrupt file.
        var chunkSize = Math.Min(_options.ChunkBytes, files.MaxChunkBytes);
        var hashes = await files.ChunkHashesAsync(fileId, chunkSize, cancellationToken).ConfigureAwait(false);

        var path = Safe(info.Name);
        var ticket = new BanterRelic(
            Name: $"{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}/{path}",
            FileId: fileId,
            Path: path,
            Length: info.Size,
            ChunkSize: chunkSize,
            FullHash: Convert.FromHexString(info.Sha256),
            ChunkHashes: hashes,
            Expires: now + _options.Lifetime);

        _tickets[ticket.Name] = ticket;
        return ticket;
    }

    public BanterRelic? Find(string relicName)
    {
        if (!_tickets.TryGetValue(relicName, out var ticket))
        {
            return null;
        }

        if (ticket.Expires > DateTimeOffset.UtcNow)
        {
            return ticket;
        }

        _tickets.TryRemove(relicName, out _);
        return null;
    }

    public async Task<byte[]?> ReadChunkAsync(
        string relicName,
        int chunkIndex,
        CancellationToken cancellationToken = default)
    {
        if (Find(relicName) is not { } ticket)
        {
            return null;
        }

        if (chunkIndex < 0 || chunkIndex >= ticket.ChunkHashes.Count)
        {
            return null;
        }

        var offset = (long)chunkIndex * ticket.ChunkSize;
        var take = (int)Math.Min(ticket.ChunkSize, ticket.Length - offset);

        try
        {
            var chunk = await files.ReadChunkAsync(ticket.FileId, offset, take).ConfigureAwait(false);
            return chunk.Data;
        }
        catch (FileStoreException)
        {
            // Deleted while a transfer was running. The fetcher sees a refusal for that chunk,
            // which is the truth.
            return null;
        }
    }

    /// <summary>
    /// Drops every live name for a file, because something happened that a name must not outlive.
    ///
    /// <para>The lifetime is the backstop, not the mechanism. A grant revoked or a file deleted is a
    /// decision someone just made, and "it stays downloadable for another five minutes" is a poor
    /// answer to it — worst of all for revoke, where the bytes are still on disk and so a stale name
    /// keeps working perfectly. Deletion is half-covered by accident (the read fails once the blob is
    /// gone); this makes both deliberate.</para>
    ///
    /// <para>What it cannot cover is the other way access ends: someone leaving the last room a file
    /// is granted to. That is a fact about a session rather than about the file, and the window there
    /// is still the ticket's own.</para>
    /// </summary>
    public void Forget(string fileId)
    {
        foreach (var (name, ticket) in _tickets)
        {
            if (string.Equals(ticket.FileId, fileId, StringComparison.Ordinal))
            {
                _tickets.TryRemove(name, out _);
            }
        }
    }

    /// <summary>A live ticket for this file with enough life left to finish a transfer.</summary>
    private BanterRelic? Reusable(string fileId, DateTimeOffset now)
    {
        var floor = now + (_options.Lifetime / 2);
        foreach (var ticket in _tickets.Values)
        {
            if (string.Equals(ticket.FileId, fileId, StringComparison.Ordinal) && ticket.Expires >= floor)
            {
                return ticket;
            }
        }

        return null;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (name, ticket) in _tickets)
        {
            if (ticket.Expires <= now)
            {
                _tickets.TryRemove(name, out _);
            }
        }
    }

    /// <summary>
    /// A file name reduced to something that can safely be part of a relic name: the rite
    /// normalises those as relative paths and refuses what it cannot, and an uploaded name is a
    /// user's string. Cut down here rather than allowed to fail a mint.
    /// </summary>
    private static string Safe(string name)
    {
        var kept = new string([.. name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')]).Trim('.');
        return kept.Length switch
        {
            0 => "file",
            > 64 => kept[^64..],
            _ => kept,
        };
    }
}
