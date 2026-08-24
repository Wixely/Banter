using CupriMark;

namespace Banter.Protocol;

/// <summary>
/// BanterProtocol's CupriMark catalogue (PLAN §4). Phase 0–2 posture: a single
/// <c>banter.core</c> component with loose ranges, unsigned — per-area catalogues
/// (files/agent/stream), signing, and lockfile gates arrive when third-party agents do
/// (Phase 5). Published ordinals are immutable; add versions, never edit them.
/// </summary>
public static class BanterCatalog
{
    public const string CoreComponent = "banter.core";

    private static readonly Component Core = new(CoreComponent,
    [
        // Ordinal 1: the v1 protocol as shipped — session/rooms/chat/streaming/files areas.
        new ComponentVersion(1, BumpReason.Functionality, VersionStatus.Active),
    ]);

    public static Catalogue Catalogue { get; } = Catalogue.Create("banter", [Core]);

    /// <summary>The contiguous ordinal range this build speaks, advertised in HELLO.</summary>
    public static OrdinalRange SupportedCore => Catalogue.Component(CoreComponent)!.Supported;

    /// <summary>Negotiates banter.core against a peer's advertised range. Both sides run this
    /// locally over the same immutable definitions, so they converge on the same ordinal.</summary>
    public static NegotiationResult NegotiateCore(ushort peerLow, ushort peerHigh) =>
        Negotiator.Negotiate(Core, OrdinalRange.Create(peerLow, peerHigh));

    /// <summary>The ranges to advertise inside HELLO.</summary>
    public static IReadOnlyList<CapabilityRangePayload> LocalRanges() =>
        [new CapabilityRangePayload(CoreComponent, SupportedCore.Min, SupportedCore.Max)];

    /// <summary>
    /// Negotiates banter.core from a peer's HELLO ranges. A peer that advertises no core range
    /// is treated as ordinal 1 (Phase 0–2 tolerance for pre-negotiation builds); a peer whose
    /// range cannot be satisfied is refused.
    /// </summary>
    public static bool TryNegotiateCore(IReadOnlyList<CapabilityRangePayload>? peerRanges, out ushort ordinal)
    {
        ordinal = 1;
        var core = peerRanges?.FirstOrDefault(r => r.Component == CoreComponent);
        if (core is null)
        {
            return true;
        }

        var result = NegotiateCore(core.Low, core.High);
        if (!result.Accepted)
        {
            return false;
        }

        ordinal = result.SelectedOrdinal;
        return true;
    }
}
