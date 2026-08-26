using Banter.Protocol;

namespace Banter.Agents.Sdk;

/// <summary>
/// The agent's half of server-side tools (PLAN §8). An agent never executes a tool: it asks the
/// server, which holds the credentials and decides whether this agent may. Everything here is
/// therefore a request, and every answer — including a refusal — is something the model reads.
/// </summary>
public abstract partial class BanterAgent
{
    private IReadOnlyList<ToolDescriptorPayload> _tools = [];

    /// <summary>
    /// The tools this agent was granted. Empty until <see cref="RefreshToolsAsync"/> has run, and
    /// empty for good on a server with no tool backend — which is the common case, so nothing
    /// here may treat "no tools" as a failure.
    /// </summary>
    protected IReadOnlyList<ToolDescriptorPayload> Tools => _tools;

    /// <summary>
    /// Ask the server what this agent may use. Called once on start; call it again after an
    /// operator changes grants, since the server does not push the change.
    /// </summary>
    protected async Task RefreshToolsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _tools = (await Client.ListToolsAsync(cancellationToken).ConfigureAwait(false)).Tools;
        }
        catch (Banter.Client.Core.BanterErrorException)
        {
            // NO_TOOLS on a server without a backend. An agent that can still talk is worth more
            // than one that refuses to start because there was nothing to grant it.
            _tools = [];
        }
    }

    /// <summary>Run a tool on the server. The room is named so the call is visible to the operator.</summary>
    protected Task<ToolResultPayload> CallToolAsync(
        string name, string arguments, string room = "", CancellationToken cancellationToken = default) =>
        Client.CallToolAsync(name, arguments, room, cancellationToken);
}
