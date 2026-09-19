using Banter.Transport.Shrine;
using CupriNet.Core;
using Xunit;

namespace Banter.Transport.Shrine.Tests;

/// <summary>
/// Which of a link's addresses a client should dial.
///
/// <para>Found on an emulator, where getting it wrong is vivid: the node advertised 127.0.0.1 as a
/// Host beacon, and on a phone that address resolves — to the phone. The dial did not fail so much
/// as succeed at nothing.</para>
/// </summary>
public sealed class MeshDialTests
{
    private static Beacon Host(string host) => new(EndpointKind.Host, host, 7772);

    [Fact]
    public void TheOperatorsOwnAddressBeatsAnythingTheNodeObserved()
    {
        var chosen = MeshDial.Preferred([
            Host("127.0.0.1"),
            new Beacon(EndpointKind.Mapped, "203.0.113.7", 7772),
            new Beacon(EndpointKind.Manual, "banter.example", 7772),
        ]);

        // PublicHost is a human saying "this is where visitors reach me". Nothing observed
        // outranks it.
        Assert.Equal("banter.example", chosen?.Host);
    }

    [Fact]
    public void AnAddressSeenFromOutsideBeatsOneSeenFromTheNodeItself()
    {
        var chosen = MeshDial.Preferred([Host("192.168.1.40"), new Beacon(EndpointKind.Mapped, "203.0.113.7", 7772)]);

        Assert.Equal("203.0.113.7", chosen?.Host);
    }

    [Fact]
    public void ALanAddressIsStillBetterThanNothing()
    {
        var chosen = MeshDial.Preferred([Host("192.168.1.40")]);

        Assert.Equal("192.168.1.40", chosen?.Host);
    }

    /// <summary>
    /// Both need a transport this client does not have. Dialling an .onion as a hostname would
    /// hang rather than fail, which is the worse of the two.
    /// </summary>
    [Fact]
    public void RelayAndOnionAreNotDialledAsHostnames()
    {
        var chosen = MeshDial.Preferred([
            new Beacon(EndpointKind.Relay, "ferryman.example", 7772),
            new Beacon(EndpointKind.Onion, "abcdef.onion", 7772),
        ]);

        Assert.Null(chosen);
    }

    [Fact]
    public void ABeaconWithNoAddressIsNotAnAddress()
    {
        Assert.Null(MeshDial.Preferred([new Beacon(EndpointKind.Manual, "   ", 7772)]));
    }

    [Fact]
    public void ALinkWithNothingDialableSaysSoAndNamesTheServer()
    {
        var failed = Assert.Throws<InvalidOperationException>(
            () => MeshDial.HostOrThrow([new Beacon(EndpointKind.Onion, "abcdef.onion", 7772)], "the-node"));

        Assert.Contains("the-node", failed.Message);
    }

    [Fact]
    public void NoBeaconsAtAllIsTheSameAnswer()
    {
        Assert.Null(MeshDial.Preferred([]));
    }
}
