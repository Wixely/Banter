namespace Banter.Protocol.Transport;

/// <summary>
/// Picks a transport from a URI scheme, for the schemes this assembly implements.
///
/// <para>One place rather than a switch in every head. It cannot cover <c>cupri://</c>, which
/// lives in <c>Banter.Transport.CupriNet</c> and depends on this assembly rather than the other
/// way round — so a head that supports the mesh resolves null itself, and every head that does not
/// gets the built-in schemes without having to remember them.</para>
/// </summary>
public static class BanterTransports
{
    /// <summary>The schemes this assembly can serve, for usage text and error messages.</summary>
    public static IReadOnlyList<string> Schemes { get; } =
        [TcpBanterTransport.Scheme, WebSocketBanterTransport.Scheme, WebSocketBanterTransport.SecureScheme];

    /// <summary>
    /// A transport for <paramref name="endpoint"/>, or null when the scheme is not one of the
    /// built-in ones. Both implementations serve either end, so one lookup answers both.
    /// </summary>
    private static object? Match(Uri endpoint) => endpoint.Scheme.ToLowerInvariant() switch
    {
        TcpBanterTransport.Scheme => new TcpBanterTransport(),
        WebSocketBanterTransport.Scheme or WebSocketBanterTransport.SecureScheme => new WebSocketBanterTransport(),
        _ => null,
    };

    public static IBanterClientTransport? TryClient(Uri endpoint) => Match(endpoint) as IBanterClientTransport;

    public static IBanterServerTransport? TryServer(Uri endpoint) => Match(endpoint) as IBanterServerTransport;

    /// <summary>
    /// A client transport, or an <see cref="ArgumentException"/> naming what it could not handle.
    /// For heads with no mesh support, where an unknown scheme is simply a mistake.
    /// </summary>
    public static IBanterClientTransport Client(Uri endpoint) =>
        TryClient(endpoint) ?? throw new ArgumentException(
            $"No transport for {endpoint.Scheme}://. Known: {string.Join(", ", Schemes)}.",
            nameof(endpoint));

    /// <summary>A server transport, or an <see cref="ArgumentException"/> as above.</summary>
    public static IBanterServerTransport Server(Uri endpoint) =>
        TryServer(endpoint) ?? throw new ArgumentException(
            $"No transport for {endpoint.Scheme}://. Known: {string.Join(", ", Schemes)}.",
            nameof(endpoint));
}
