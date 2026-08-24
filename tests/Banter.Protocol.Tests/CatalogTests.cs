using Banter.Protocol;
using Xunit;

namespace Banter.Protocol.Tests;

public sealed class CatalogTests
{
    [Fact]
    public void CoreComponentSpeaksRevisionOne()
    {
        var range = BanterCatalog.SupportedCore;
        Assert.Equal(1, range.Min);
        Assert.Equal(1, range.Max);
    }

    [Fact]
    public void LocalRangesAdvertiseCore()
    {
        var advertised = Assert.Single(BanterCatalog.LocalRanges());
        Assert.Equal(BanterCatalog.CoreComponent, advertised.Component);
        Assert.Equal(BanterCatalog.SupportedCore.Min, advertised.Low);
        Assert.Equal(BanterCatalog.SupportedCore.Max, advertised.High);
    }

    [Fact]
    public void OverlappingPeerNegotiatesRevisionOne()
    {
        Assert.True(BanterCatalog.TryNegotiateCore([new CapabilityRangePayload(BanterCatalog.CoreComponent, 1, 5)], out var ordinal));
        Assert.Equal(1, ordinal);
    }

    [Fact]
    public void DisjointPeerIsRefused()
    {
        Assert.False(BanterCatalog.TryNegotiateCore([new CapabilityRangePayload(BanterCatalog.CoreComponent, 7, 9)], out _));
    }

    [Fact]
    public void PeerWithoutRangesGetsPhaseZeroTolerance()
    {
        Assert.True(BanterCatalog.TryNegotiateCore(null, out var ordinal));
        Assert.Equal(1, ordinal);

        Assert.True(BanterCatalog.TryNegotiateCore([new CapabilityRangePayload("banter.files", 1, 1)], out ordinal));
        Assert.Equal(1, ordinal);
    }
}
