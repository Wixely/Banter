using System.Text.Json;

namespace Banter.Server.Tools;

/// <summary>
/// Loads the MCP upstream list from a JSON file. A file rather than command-line flags because
/// the list is deployment shape — which servers exist, under which keys — and it is mounted into
/// a container alongside the secrets those servers need.
/// </summary>
public static class McpConfigFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record ConfigShape(List<McpUpstreamConfig>? Upstreams);

    /// <summary>
    /// Read the file, or return empty options when it is absent. A missing config means "no
    /// tools", not a failure to start: most deployments will not have MCP servers at all, and a
    /// server that refuses to boot without one would be exactly the wrong default.
    /// </summary>
    public static McpOptions Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new McpOptions();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ConfigShape>(File.ReadAllText(path), Options);
            var upstreams = parsed?.Upstreams ?? [];

            // A duplicate key would mean two servers claiming the same prefix, and the second
            // would silently shadow the first — say so rather than aggregate a surprise.
            foreach (var duplicate in upstreams.GroupBy(u => u.Key, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                Console.Error.WriteLine($"mcp: '{duplicate.Key}' is configured more than once; using the first.");
            }

            return new McpOptions
            {
                Upstreams = upstreams
                    .DistinctBy(u => u.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Same reasoning as a missing file: the chat server is useful without tools, and a
            // crash-loop over a typo in an optional file helps nobody.
            Console.Error.WriteLine($"mcp: could not read {path}: {ex.Message}");
            return new McpOptions();
        }
    }
}
