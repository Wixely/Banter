using System.Text.Json;
using MCPHub.Proxy;
using ModelContextProtocol.Protocol;

namespace Banter.Server.Tools;

/// <summary>A hub refused a management call, and said why in terms meant to be acted on.</summary>
/// <param name="Code">The hub's stable code, e.g. <c>users.read_only</c>, to branch on rather than
/// matching prose.</param>
public sealed class HubAdminException(string code, string message, string? remedy = null)
    : Exception(remedy is { Length: > 0 } ? $"{message} {remedy}" : message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Calling a hub's management tools as Banter.
///
/// <para>An interface because what it does is make a network call to a running MCPHub, and the
/// decisions built on top of it — when to mint a user, what to do when a hub refuses — are worth
/// testing without one.</para>
/// </summary>
public interface IHubAdmin
{
    /// <summary>
    /// Runs one management tool and returns its JSON payload.
    /// </summary>
    /// <exception cref="HubAdminException">The hub refused, or does not offer the tool at all.</exception>
    Task<JsonElement> CallAsync(
        string tool, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IHubAdmin"/> over an upstream this server has already connected — Banter's own hub
/// user, which is the one that holds the administration tools.
/// </summary>
public sealed class McpHubAdmin(UpstreamRegistry registry, string upstreamKey) : IHubAdmin
{
    /// <summary>
    /// The namespaced name of a management tool on this hub. The prefix is the upstream's key
    /// here, not the hub's own: an MCPHub proxied under <c>hub</c> offers <c>hub__users__create</c>.
    /// </summary>
    public string Qualify(string tool) => upstreamKey + ProxyConstants.NamespaceSeparator + tool;

    /// <inheritdoc />
    public async Task<JsonElement> CallAsync(
        string tool,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken = default)
    {
        var exposed = Qualify(tool);
        if (!registry.Catalog.Routes.TryGetValue(exposed, out var route))
        {
            // Absent rather than refused is what an unauthorized tool looks like through MCP, so
            // this is the message for "administration is off" as well as "wrong upstream". Both
            // are fixed in the hub, and saying which one to look at is the whole point.
            throw new HubAdminException(
                "hub.unavailable",
                $"This hub does not offer '{exposed}'.",
                "Turn on its administration tools (MCPHUB_ADMINISTRATION_ENABLED, or the switch on "
                + "the Users page) and grant them to the user Banter connects as.");
        }

        var result = await route.Client
            .CallToolAsync(route.OriginalName, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (text.Length == 0)
        {
            throw new HubAdminException("hub.empty", $"'{exposed}' returned nothing to read.");
        }

        var payload = JsonDocument.Parse(text).RootElement.Clone();
        if (result.IsError ?? false)
        {
            // The hub's own shape: a code to branch on, what is wrong, and what to change.
            throw new HubAdminException(
                Text(payload, "code") ?? "hub.failed",
                Text(payload, "reason") ?? text,
                Text(payload, "remedy"));
        }

        return payload;
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
