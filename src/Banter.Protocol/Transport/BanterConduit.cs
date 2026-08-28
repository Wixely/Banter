namespace Banter.Protocol.Transport;

/// <summary>
/// What identifies BanterProtocol on a CupriNet conduit.
///
/// <para>Here, rather than in either transport, because two of them carry Banter over a conduit —
/// the mesh transport over an <c>ArcanumSession</c>, and the Shrine transport over a Nodestar
/// site — and they must agree. They did not: each had its own constant, and the values differed.
/// A mismatch is not a quiet incompatibility either, since Nodestar <b>seals the peer</b> on an
/// id it does not serve, so one side would simply be shown the door.</para>
/// </summary>
public static class BanterConduit
{
    /// <summary>
    /// Banter's conduit protocol id. Chosen freely — the rite keeps no registry — and fixed by
    /// use: this is the value the mesh transport has always sent, so it is the one both keep.
    /// </summary>
    public const uint ProtocolId = 0xBA17;
}
