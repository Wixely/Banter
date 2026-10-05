namespace Banter.Server.Tools;

/// <summary>
/// How an upstream proves who Banter is.
///
/// <para>An MCP server that checks identity — MCPHub with per-user permissions on, say — wants a
/// bearer key, and what that key is allowed to do is decided there rather than here. So this
/// resolves one, from the config file or from somewhere the config file only points at.</para>
///
/// <para>Three sources because deployments differ and only one of them is good everywhere: the
/// literal value for a file that is already a mounted secret, a file for a Docker or Kubernetes
/// secret, an environment variable for a compose file. Naming two is refused rather than ranked —
/// a deployment that sets both has an opinion that is about to be ignored.</para>
/// </summary>
public static class McpUpstreamCredentials
{
    /// <summary>
    /// The token for this upstream, or null when it has no credentials configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">A source is named and cannot be read, or more
    /// than one is named. Connecting anyway is the thing not to do: the server either refuses
    /// Banter — which looks like the server being down — or, worse, serves it anonymously and
    /// hands over whatever an unauthenticated caller gets.</exception>
    public static string? Resolve(McpUpstreamConfig upstream, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        var read = environment ?? Environment.GetEnvironmentVariable;

        var named = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(upstream.Token)) named.Add(nameof(upstream.Token));
        if (!string.IsNullOrWhiteSpace(upstream.TokenFile)) named.Add(nameof(upstream.TokenFile));
        if (!string.IsNullOrWhiteSpace(upstream.TokenEnvironmentVariable))
            named.Add(nameof(upstream.TokenEnvironmentVariable));

        if (named.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{upstream.Key}' names more than one token source ({string.Join(" and ", named)}). Choose one.");
        }

        if (named.Count == 0)
        {
            return null;
        }

        var token = named[0] switch
        {
            nameof(upstream.Token) => upstream.Token,
            nameof(upstream.TokenFile) => ReadFile(upstream.Key, upstream.TokenFile!),
            _ => read(upstream.TokenEnvironmentVariable!),
        };

        if (string.IsNullOrWhiteSpace(token))
        {
            // Named and empty is a deployment that meant to authenticate and has not, which is
            // worth stopping for. No value is quoted: this one is the credential.
            throw new InvalidOperationException(
                $"'{upstream.Key}' names {named[0]} but it is empty or missing.");
        }

        return token.Trim();
    }

    /// <summary>The headers for an HTTP upstream: a bearer token, or nothing at all.</summary>
    public static IReadOnlyDictionary<string, string>? Headers(string? token) =>
        token is { Length: > 0 }
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token }
            : null;

    private static string ReadFile(string key, string path)
    {
        try
        {
            // Trailing newline trimmed: `echo secret > file` is how most of these are made, and a
            // key with a newline on the end fails authentication in a way nothing explains.
            return File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"'{key}' could not read its token file: {ex.Message}");
        }
    }
}
