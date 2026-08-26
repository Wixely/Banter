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
}
