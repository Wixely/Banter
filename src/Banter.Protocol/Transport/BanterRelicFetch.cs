namespace Banter.Protocol.Transport;

/// <summary>
/// A connection that can also fetch bulk content <b>beside</b> the frame pipe, by name (PLAN §2.5).
///
/// <para>This is the client half of CupriNet's Relic rite, stated without CupriNet in it. Only the
/// Shrine transport implements it — a relic is a rite on the same authenticated visit the conduit
/// rides, so there is nothing for a TCP or WebSocket connection to delegate to — and that is why it
/// is separate from <see cref="IBanterConnection"/>: a caller pattern-matches for it and falls back
/// to the frame pipe when it is absent, rather than every transport having to answer for a
/// capability one of them has.</para>
///
/// <para>Two things it buys over chunking across the frame pipe, and both are why §2.5 asks for it:
/// every chunk is verified against a manifest as it lands, and the transfer runs on its own logical
/// stream — so someone downloading a video does not stall the room's chat behind it.</para>
/// </summary>
public interface IBanterRelicFetch
{
    /// <summary>
    /// Fetches the named relic whole, verifying it against its own manifest.
    ///
    /// <para><paramref name="maxBytes"/> is a ward, not a hint: the manifest arrives from the far
    /// end and is what the buffer is sized from, so an unbounded fetch in a browser tab is one
    /// hostile or broken manifest away from an out-of-memory. Callers know the size they asked for
    /// and should pass it.</para>
    /// </summary>
    Task<byte[]> FetchRelicAsync(string relicName, long maxBytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// One named relic and everything a transfer of it needs to be verified — a chunked file described
/// without a transport type or a storage type in it.
///
/// <para><see cref="ChunkHashes"/> and <see cref="FullHash"/> are the promise: a receiver checks
/// each chunk as it lands and the whole file before it uses it, so a source that answers wrongly
/// can only fail a transfer, never corrupt one. Both are SHA-256 — the algorithm the rite verifies
/// with, and the one the file store already names its blobs by.</para>
/// </summary>
public sealed record BanterRelic(
    string Name,
    string FileId,
    string Path,
    long Length,
    int ChunkSize,
    byte[] FullHash,
    IReadOnlyList<byte[]> ChunkHashes,
    DateTimeOffset Expires);

/// <summary>
/// The server half of the same seam: resolve a name, read a chunk. Deliberately the whole of it —
/// an adapter implementing CupriNet's <c>IRelicSource</c> over this needs nothing else, which is
/// what keeps the transport ignorant of the file store and the file store ignorant of CupriNet.
///
/// <para>Notice what is <i>not</i> here: who is asking. The rite that serves these is answered by a
/// node-wide source that is handed a name and nothing more, which is why a relic name has to be the
/// authority — minted on the conduit, where the session is authenticated, and short-lived. See
/// <c>FileRelics</c>.</para>
/// </summary>
public interface IBanterRelicSource
{
    /// <summary>The relic for a name, or null when it was never minted or has expired.</summary>
    BanterRelic? Find(string relicName);

    /// <summary>One chunk of a relic, or null when the name or the index does not resolve.</summary>
    Task<byte[]?> ReadChunkAsync(string relicName, int chunkIndex, CancellationToken cancellationToken = default);
}
