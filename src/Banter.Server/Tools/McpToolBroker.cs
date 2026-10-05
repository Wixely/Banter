using System.Collections.Concurrent;
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

    /// <summary>
    /// The bearer token for an HTTP upstream that checks who is calling — MCPHub with per-user
    /// permissions on, say, where this is the key issued to Banter's user and what it may reach is
    /// decided there. Exactly one of this, <see cref="TokenFile"/> or
    /// <see cref="TokenEnvironmentVariable"/>; see <see cref="McpUpstreamCredentials"/>.
    /// </summary>
    public string? Token { get; init; }

    /// <inheritdoc cref="Token"/>
    public string? TokenFile { get; init; }

    /// <inheritdoc cref="Token"/>
    public string? TokenEnvironmentVariable { get; init; }

    /// <summary>
    /// Environment for a stdio upstream, applied on top of the one this process inherited. The
    /// right place for that server's own secrets: anything on a command line is visible to any
    /// process listing on the box.
    /// </summary>
    public Dictionary<string, string?> Environment { get; init; } = [];

    /// <summary>
    /// Whether this upstream decides for itself what each agent may use — MCPHub with per-user
    /// permissions on.
    ///
    /// <para>Set, every agent gets a user and a key of its own there, its calls are made as that
    /// agent, and <b>the upstream enforces</b>: what it lists is what that agent may see, and what
    /// it refuses is refused there rather than here. Banter's own grants do not apply to its tools,
    /// because two answers to one question is how they come to disagree.</para>
    ///
    /// <para>Unset — the default, and right for every MCP server that has no idea who is calling —
    /// the tools are granted per agent by this server, out of its own store.</para>
    /// </summary>
    public bool PerAgentIdentity { get; init; }
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
    private readonly HubIdentityStore? _identities;
    private readonly UpstreamRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>One session per agent per self-enforcing upstream, opened on first use and kept.
    /// Keyed by agent: the sessions an agent has are made and torn down together.</summary>
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<AgentUpstream>>> _agentSessions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<McpUpstreamConfig> _selfEnforcing = [];
    private bool _connected;

    public McpToolBroker(
        McpOptions options,
        ToolGrantStore grants,
        HubIdentityStore? identities = null,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _grants = grants;
        _identities = identities;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _registry = new UpstreamRegistry(_loggerFactory);
    }

    /// <summary>An agent's own session with an upstream that enforces for itself.</summary>
    private sealed record AgentUpstream(string Key, UpstreamRegistry Registry);

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
                    // Resolved before connecting, and a failure here skips this upstream rather
                    // than connecting without it: a server that was told to expect a key either
                    // refuses Banter — which reads as the server being down — or serves it
                    // anonymously and hands over whatever an unauthenticated caller gets.
                    var headers = McpUpstreamCredentials.Headers(McpUpstreamCredentials.Resolve(upstream));

                    await _registry.ConnectAsync(
                        upstream.Key,
                        upstream.DisplayName.Length > 0 ? upstream.DisplayName : upstream.Key,
                        new Uri(url),
                        headers,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (upstream.Command is { Length: > 0 } command)
                {
                    await _registry.ConnectStdioAsync(
                        upstream.Key,
                        upstream.DisplayName.Length > 0 ? upstream.DisplayName : upstream.Key,
                        command,
                        upstream.Arguments,
                        upstream.Environment.Count > 0 ? upstream.Environment : null,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"mcp: '{upstream.Key}' did not connect: {ex.Message}");
            }
        }

        // Noted rather than acted on: an agent's own session with one of these is opened the first
        // time that agent asks for anything, because most agents never will.
        _selfEnforcing.AddRange(_options.Upstreams.Where(u => u is { PerAgentIdentity: true, Url.Length: > 0 }));
        if (_selfEnforcing.Count > 0 && _identities is null)
        {
            // Configured to defer to an upstream with nowhere to remember who the agent is there.
            // Said once, loudly: the alternative is every agent silently getting no tools from it.
            Console.Error.WriteLine(
                "mcp: "
                + string.Join(", ", _selfEnforcing.Select(u => $"'{u.Key}'"))
                + " asked for per-agent identities, but this server has no store for them; their tools "
                + "will not be offered.");
            _selfEnforcing.Clear();
        }

        _connected = true;
    }

    /// <summary>
    /// The agent's own sessions with the upstreams that enforce for themselves, opened on first
    /// use.
    ///
    /// <para>One session per agent, not one shared one: the key is the identity, so a shared
    /// session could only ever be one agent. An agent whose identity cannot be minted — the hub
    /// refusing, its administration switched off — contributes nothing rather than falling back to
    /// this server's own idea of what it may use, which would be exactly the second answer this
    /// arrangement exists to remove.</para>
    /// </summary>
    private Task<IReadOnlyList<AgentUpstream>> SessionsForAsync(string agent, Action<string>? audit = null) =>
        _agentSessions.GetOrAdd(agent, name => OpenSessionsAsync(name, audit));

    private async Task<IReadOnlyList<AgentUpstream>> OpenSessionsAsync(string agent, Action<string>? audit)
    {
        var opened = new List<AgentUpstream>();
        foreach (var upstream in _selfEnforcing)
        {
            try
            {
                var provisioner = new HubProvisioner(new McpHubAdmin(_registry, upstream.Key), _identities!, upstream.Key);
                var identity = await provisioner.EnsureAsync(agent).ConfigureAwait(false);

                var registry = new UpstreamRegistry(_loggerFactory);
                await registry.ConnectAsync(
                    upstream.Key,
                    upstream.DisplayName.Length > 0 ? upstream.DisplayName : upstream.Key,
                    new Uri(upstream.Url!),
                    McpUpstreamCredentials.Headers(identity.Token)).ConfigureAwait(false);

                opened.Add(new AgentUpstream(upstream.Key, registry));
            }
            catch (Exception ex)
            {
                // Nothing from this upstream for this agent, and a line saying why. Not fatal: the
                // other upstreams are still worth having, and an agent with no tools at all is a
                // state the room can see.
                audit?.Invoke($"{agent} has no access to '{upstream.Key}': {ex.Message}");
                Console.Error.WriteLine($"mcp: '{upstream.Key}' has no session for {agent}: {ex.Message}");
            }
        }

        return opened;
    }

    /// <summary>
    /// Forgets an agent's sessions, closing them. Called when its identity changes — a rotated key
    /// leaves the open session authenticated as nobody.
    /// </summary>
    public async Task ForgetAgentAsync(string agent)
    {
        if (_agentSessions.TryRemove(agent, out var sessions))
        {
            foreach (var session in await sessions.ConfigureAwait(false))
            {
                await session.Registry.DisconnectAllAsync().ConfigureAwait(false);
            }
        }
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

        var tools = _registry.Catalog.Tools
            .Where(t => granted.Contains(t.Name))
            .Select(Describe)
            .ToList();

        // An upstream that enforces for itself has already decided: what it lists to this agent's
        // own session IS what that agent may use, so there is nothing here to filter it by. Adding
        // this server's grants on top would be a second opinion that can only ever take something
        // away that the authority had allowed.
        foreach (var session in await SessionsForAsync(agent).ConfigureAwait(false))
        {
            var catalog = session.Registry.Catalog;
            tools.AddRange(catalog.Tools.Select(t => Describe(t, session.Key)));
        }

        return tools;
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
        // The agent's own session first: for those upstreams the call is made AS the agent, and
        // the refusal — if there is one — is theirs. This server does not get a vote, which is what
        // makes an agent that went around it and called the upstream directly get the same answer.
        foreach (var session in await SessionsForAsync(agent, audit).ConfigureAwait(false))
        {
            if (session.Registry.Catalog.Routes.TryGetValue(call.Name, out var own))
            {
                return await InvokeAsync(agent, call, own, audit, cancellationToken).ConfigureAwait(false);
            }
        }

        if (IsSelfEnforcing(call.Name))
        {
            // Named by an upstream that decides for itself, and absent from this agent's session —
            // so that upstream did not offer it to this agent. Same answer as an ungranted tool
            // below, and for the same reason: a distinguishable "exists but not for you" maps the
            // estate an agent was kept out of.
            audit?.Invoke($"{agent} was refused '{call.Name}' (not granted)");
            return new ToolResultPayload(call.Name, $"'{call.Name}' is not available to you.", IsError: true);
        }

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

        return await InvokeAsync(agent, call, route, audit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a tool belongs to an upstream that decides for itself who may use it.</summary>
    private bool IsSelfEnforcing(string toolName) => _selfEnforcing.Any(
        u => toolName.StartsWith(u.Key + ProxyConstants.NamespaceSeparator, StringComparison.Ordinal));

    private async Task<ToolResultPayload> InvokeAsync(
        string agent,
        ToolCallPayload call,
        ToolRoute route,
        Action<string>? audit,
        CancellationToken cancellationToken)
    {
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
    private ToolDescriptorPayload Describe(Tool tool) => Describe(
        tool,
        _registry.Catalog.Routes.TryGetValue(tool.Name, out var route) ? route.ServerKey : "");

    private static ToolDescriptorPayload Describe(Tool tool, string serverKey) => new(
        tool.Name,
        tool.Description ?? "",
        tool.InputSchema.ToString() ?? "{}",
        serverKey);

    public async ValueTask DisposeAsync()
    {
        foreach (var agent in _agentSessions.Keys)
        {
            await ForgetAgentAsync(agent).ConfigureAwait(false);
        }

        await _registry.DisconnectAllAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
