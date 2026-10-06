using Banter.Protocol;

namespace Banter.Server.Tools;

/// <summary>
/// What the room engine needs of a tool backend. An interface rather than the concrete
/// <see cref="McpToolBroker"/> so the engine's authorization rules can be tested without
/// standing up real MCP servers — the rules are the part that must not regress.
/// </summary>
public interface IToolBroker
{
    /// <summary>The tools this agent may use. Ungranted tools are absent, not marked.</summary>
    Task<IReadOnlyList<ToolDescriptorPayload>> ToolsForAsync(string agent, CancellationToken cancellationToken = default);

    /// <summary>Every connected tool, ignoring grants. For operators, never for agents.</summary>
    IReadOnlyList<ToolDescriptorPayload> AllTools();

    /// <summary>Run a tool for an agent, if it is granted.</summary>
    Task<ToolResultPayload> CallAsync(
        string agent, ToolCallPayload call, Action<string>? audit = null, CancellationToken cancellationToken = default);

    /// <summary>Which tool names an agent currently holds.</summary>
    Task<IReadOnlyList<string>> GrantsForAsync(string agent, CancellationToken cancellationToken = default);

    /// <summary>Replace an agent's grants wholesale. An empty list revokes everything.</summary>
    Task SetGrantsAsync(string agent, IReadOnlyList<string> tools, CancellationToken cancellationToken = default);

    /// <summary>
    /// The hubs this server defers to — those that decide for themselves what each agent may use —
    /// with their reachability and the agents that have an identity on them. Empty when there are
    /// none, which is every deployment that grants its tools here.
    /// </summary>
    Task<IReadOnlyList<HubPayload>> InspectHubsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Issue an agent a new key on one hub, retiring the one it holds, and drop whatever it has
    /// open there. The new key never leaves the server.
    /// </summary>
    /// <exception cref="HubAdminException">No such hub here, or the hub refused.</exception>
    Task RotateHubKeyAsync(string hub, string agent, CancellationToken cancellationToken = default);

    /// <summary>Remove an agent's identity from one hub - deleted there, forgotten here.</summary>
    /// <exception cref="HubAdminException">No such hub here, or the hub refused.</exception>
    Task RemoveHubIdentityAsync(string hub, string agent, CancellationToken cancellationToken = default);
}
