using Banter.Protocol.Transport;
using CupriNet.Nodestar;
using CupriNet.Rites;

namespace Banter.Transport.Shrine;

/// <summary>
/// Banter's files, served as CupriNet relics (PLAN §2.5).
///
/// <para>All of the transfer's substance — chunking, per-chunk hashes, the whole-file hash, resume,
/// and the stream it runs on — belongs to the rite. This class is the seam and nothing else: it
/// turns a <see cref="BanterRelic"/> into the manifest the rite publishes, and forwards chunk reads.
/// The reason it is so thin is the reason it is worth having: Banter's own transfer was 64 KB frames
/// carrying an offset with no integrity and no resume, and replacing it means <i>deleting</i> that
/// logic rather than reimplementing it somewhere better.</para>
///
/// <para><b>One file per relic.</b> The rite carries bundles — a manifest may describe many files —
/// but a Banter file is one file, and a room's attachments are not a set anyone fetches at once. So
/// file index 0 is the only one that resolves, and anything else is a request for something this
/// source does not have.</para>
///
/// <para><b>The hashes are not recomputed here.</b> They arrive already computed, with SHA-256,
/// which is what the rite verifies with and what the file store already names its blobs by. Hashing
/// again through the suite would be the same bytes at the cost of a second pass over every file.</para>
/// </summary>
public sealed class ShrineRelicSource(IBanterRelicSource relics) : IRelicSource
{
    public ValueTask<ReliquaryManifest?> ResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        if (relics.Find(name) is not { } relic)
        {
            // Never minted, expired, or guessed at. The rite answers "no such relic" either way,
            // which is the only answer that does not distinguish the three.
            return ValueTask.FromResult<ReliquaryManifest?>(null);
        }

        return ValueTask.FromResult<ReliquaryManifest?>(new ReliquaryManifest
        {
            // Derived rather than random, so resolving the same relic twice describes the same
            // transfer — a client that re-fetches a manifest after a dropped connection is
            // resuming, not starting something new. The content hash is public to anyone holding
            // the name, and to anyone who can see the file's metadata at all.
            TransferId = relic.FullHash.Length >= 16 ? relic.FullHash[..16] : relic.FullHash,
            Files =
            [
                new ReliquaryFile
                {
                    RelativePath = relic.Path,
                    Length = relic.Length,
                    ChunkSize = relic.ChunkSize,
                    FullHash = relic.FullHash,
                    ChunkHashes = relic.ChunkHashes,
                },
            ],
        });
    }

    public ValueTask<byte[]?> ReadChunkAsync(
        string name,
        int fileIndex,
        int chunkIndex,
        CancellationToken cancellationToken = default) =>
        fileIndex == 0
            ? new ValueTask<byte[]?>(relics.ReadChunkAsync(name, chunkIndex, cancellationToken))
            : ValueTask.FromResult<byte[]?>(null);
}

/// <summary>Puts Banter's files on the site's Relic rite.</summary>
public static class ShrineRelicSite
{
    /// <summary>
    /// Serves <paramref name="relics"/> as this site's relics, beside the conduit
    /// <c>ServeBanter</c> registers.
    ///
    /// <para>The suite the site hands over is not used: the manifests are already hashed with
    /// SHA-256, which is what the rite verifies with.</para>
    /// </summary>
    public static SiteBuilder ServeBanterRelics(this SiteBuilder site, IBanterRelicSource relics)
        => site.ServeRelics(_ => new ShrineRelicSource(relics));
}
