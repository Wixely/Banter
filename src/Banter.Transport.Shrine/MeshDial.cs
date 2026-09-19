using CupriNet.Core;

namespace Banter.Transport.Shrine;

/// <summary>
/// Reading a dialable address out of a signed CupriNet link.
///
/// <para>A link names the addresses a node believes it can be reached at, and does not say which
/// port serves which rite — so a client that dials a vessel supplies the port itself and takes
/// only the host from here. The browser has the same split: it reads a host out of the beacons and
/// gets the WebRTC port from elsewhere.</para>
/// </summary>
public static class MeshDial
{
    /// <summary>
    /// Which beacon to dial, most likely to work first.
    ///
    /// <para><see cref="EndpointKind"/> is documented as being in "rough order of connection
    /// preference", and for a client reaching a node that order is backwards: it runs from the
    /// node's own LAN address outward, and a client outside that LAN wants the outermost one. So
    /// this walks it the other way — <b>Manual</b> first, because it is the one a human configured
    /// as the address visitors reach (Nodestar's <c>PublicHost</c>) and beats anything observed;
    /// then <b>Mapped</b>, which is the node seen from outside; then <b>Host</b>, which is only
    /// reachable from the same network.</para>
    ///
    /// <para>Getting this backwards is not a subtle failure, but it is a confusing one: a node
    /// behind any NAT advertises a Host beacon that is perfectly valid and completely unreachable,
    /// and an emulator makes it worse by making that address — 127.0.0.1 — resolve on the phone,
    /// to the phone.</para>
    ///
    /// <para>Relay and Onion are skipped: both need a transport this does not have, and dialling
    /// an .onion as a hostname would hang rather than fail.</para>
    /// </summary>
    public static Beacon? Preferred(IEnumerable<Beacon> beacons)
    {
        ArgumentNullException.ThrowIfNull(beacons);

        var candidates = beacons
            .Where(b => !string.IsNullOrWhiteSpace(b.Host))
            .ToList();

        return First(candidates, EndpointKind.Manual)
            ?? First(candidates, EndpointKind.Mapped)
            ?? First(candidates, EndpointKind.Host);
    }

    /// <summary>
    /// The host to dial, or a message saying why there is none. Thrown rather than returned as
    /// null because every caller is inside a dial and has nothing useful to do with an absence.
    /// </summary>
    public static string HostOrThrow(IEnumerable<Beacon> beacons, string moniker) =>
        Preferred(beacons)?.Host
        ?? throw new InvalidOperationException(
            $"The link for '{moniker}' names no address this client can dial.");

    private static Beacon? First(List<Beacon> beacons, EndpointKind kind) =>
        beacons.FirstOrDefault(b => b.Kind == kind);
}
