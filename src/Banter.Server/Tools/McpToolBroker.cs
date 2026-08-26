using System.Text.Json;
using Banter.Protocol;
using Banter.Server.Persistence;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Banter.Server.Tools;

/// <summary>One MCP server to aggregate. HTTP when <see cref="Url"/> is set, else stdio.</summary>
public sealed record McpUpstreamConfig
{
    public required string Key { get; init; }
    public string DisplayName { get; init; } = "";
    public string? Url { get; init; }
    public string? Command { get; init; }
    public List<string> Arguments { get; init; } = [];
}

public sealed record McpOptions
{
    public List<McpUpstreamConfig> Upstreams { get; init; } = [];

    /// <summary>
    /// How long a single tool call may run. A tool that hangs would otherwise hold an agent's
    /// turn open indefinitely, and the room would just look silent.
    /// </summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Cap on returned content, so one enormous result cannot swamp an agent's context.</summary>
    public int MaxResultChars { get; init; } = 16_000;
}

/// <summary>
/// Runs MCP tools on behalf of agents (PLAN §8).
///
/// <para><b>Tools execute here, never on the agent.</b> The credentials for an MCP server — API
/// tokens, database connections — live on the server, so an agent that held them could act
/// outside anything Banter can see or audit. Agents ask; the server decides and does.</para>
///
/// <para>Authorization is per agent account, and an ungranted tool is <em>absent</em> from the
/// listing rather than refused on call, so an agent cannot discover what it may not use.</para>
/// </summary>
public sealed class McpToolBroker : IToolBroker, IAsyncDisposable
{
    private readonly McpOptions _options;
    private readonly ToolGrantStore _grants;
    private readonly UpstreamRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private bool _connected;

    public McpToolBroker(McpOptions options, ToolGrantStore grants, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _grants = grants;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _registry = new UpstreamRegistry(_loggerFactory);
    }

    /// <summary>Upstreams that connected, for the management UI.</summary>
    public IReadOnlyCollection<UpstreamServer> Upstreams => _registry.Upstreams;

    /// <summary>Every aggregated tool, ignoring grants. For the operator, not for agents.</summary>
    public IReadOnlyList<ToolDescriptorPayload> AllTools() =>
        _registry.Catalog.Tools.Select(Describe).ToList();

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GrantsForAsync(string agent, CancellationToken cancellationToken = default) =>
        _grants.ForAgentAsync(agent, cancellationToken);

    /// <inheritdoc />
    public Task SetGrantsAsync(
        string agent, IReadOnlyList<string> tools, CancellationToken cancellationToken = default) =>
        _grants.ReplaceAsync(agent, tools, cancellationToken);

    /// <summary>
    /// Connect the configured upstreams. One failing does not stop the others: losing a GitHub
    /// server should not take away an agent's filesystem tools.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var upstream in _options.Upstreams)
        {
            try
            {
                if (upstream.Url is { Length: > 0 } url)
                {
                    await _registry.ConnectAsync(
                        upstream.Key,
                        upstream.DisplayName.Length > 0 ? upstream.DisplayName : upstream.Key,
                        new Uri(url),
                        cancellationToken).ConfigureAwait(false);
                }
                else if (upstream.Command is { Length: > 0 } command)
                {
                    await _registry.ConnectStdioAsync(
                        upstream.Key,
                        upstream.DisplayName.Length > 0 ? upstream.DisplayName : upstream.Key,
                        command,
                        upstream.Arguments,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"mcp: '{upstream.Key}' did not connect: {ex.Message}");
            }
        }

        _connected = true;
    }

    /// <summary>
    /// The tools this agent may use. Absent grants mean an empty list, not everything — a new
    /// agent must not inherit access to whatever the server happens to have connected.
    /// </summary>
    public async Task<IReadOnlyList<ToolDescriptorPayload>> ToolsForAsync(
        string agent, CancellationToken cancellationToken = default)
    {
        if (!_connected)
        {
            return [];
        }

        var granted = (await _grants.ForAgentAsync(agent, cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        return _registry.Catalog.Tools
            .Where(t => granted.Contains(t.Name))
            .Select(Describe)
            .ToList();
    }

    /// <summary>
    /// Run a tool for an agent, if it is granted. Every refusal and every call is reported to
    /// <paramref name="audit"/> so an operator can see what agents actually did.
    /// </summary>
    public async Task<ToolResultPayload> CallAsync(
        string agent,
        ToolCallPayload call,
        Action<string>? audit = null,
        CancellationToken cancellationToken = default)
    {
        var granted = (await _grants.ForAgentAsync(agent, cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        if (!granted.Contains(call.Name))
        {
            // Same answer whether the tool exists or is merely ungranted: a distinguishable
            // "no such tool" would let an agent map what the server has connected.
            audit?.Invoke($"{agent} was refused '{call.Name}' (not granted)");
            return new ToolResultPayload(call.Name, $"'{call.Name}' is not available to you.", IsError: true);
        }

        if (!_registry.Catalog.Routes.TryGetValue(call.Name, out var route))
        {
            audit?.Invoke($"{agent} called '{call.Name}' but its server is not connected");
            return new ToolResultPayload(call.Name, $"'{call.Name}' is not currently connected.", IsError: true);
        }

        IReadOnlyDictionary<string, object?>? arguments;
        try
        {
            // Boxed as object? because that is what the MCP client takes; the values stay
            // JsonElement so the upstream sees exactly the JSON the model produced.
            arguments = call.Arguments.Length == 0
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(call.Arguments)
                    ?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        }
        catch (JsonException ex)
        {
            // Models produce malformed JSON often enough that this must be an ordinary answer the
            // model can read and retry from, not an exception that kills the turn.
            return new ToolResultPayload(call.Name, $"Arguments were not valid JSON: {ex.Message}", IsError: true);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CallTimeout);

        try
        {
            // Arguments are deliberately not logged: they routinely carry file contents and
            // credentials, and the audit line is meant to be readable by an operator.
            audit?.Invoke($"{agent} called '{call.Name}' ({route.ServerKey})");

            var result = await route.Client.CallToolAsync(
                route.OriginalName, arguments, cancellationToken: timeout.Token).ConfigureAwait(false);

            return new ToolResultPayload(call.Name, Flatten(result), result.IsError ?? false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ToolResultPayload(
                call.Name, $"Timed out after {_options.CallTimeout.TotalSeconds:0}s.", IsError: true);
        }
        catch (Exception ex)
        {
            return new ToolResultPayload(call.Name, ex.Message, IsError: true);
        }
    }

    /// <summary>Flatten MCP content blocks into text a model can read, capped in size.</summary>
    private string Flatten(CallToolResult result)
    {
        var text = string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>().Select(c => c.Text));

        if (text.Length == 0)
        {
            // A tool that returned only non-text content still succeeded; saying nothing at all
            // reads to the model as a failure.
            text = result.Content.Count > 0
                ? $"({result.Content.Count} non-text result block(s))"
                : "(no output)";
        }

        return text.Length <= _options.MaxResultChars
            ? text
            : text[.._options.MaxResultChars] + "\n… (truncated)";
    }

    /// <summary>
    /// Describe a tool for the wire. The server key comes from the route rather than the tool
    /// itself, because the management UI groups by upstream and an unattributed tool would give
    /// an operator no way to tell which server they are actually granting access to.
    /// </summary>
    private ToolDescriptorPayload Describe(Tool tool) => new(
        tool.Name,
        tool.Description ?? "",
        tool.InputSchema.ToString() ?? "{}",
        _registry.Catalog.Routes.TryGetValue(tool.Name, out var route) ? route.ServerKey : "");

    public async ValueTask DisposeAsync()
    {
        await _registry.DisconnectAllAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
