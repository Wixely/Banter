namespace Banter.App;

/// <summary>
/// The tool-grants panel's state (PLAN §8). Grants are edited locally and sent in one go, so an
/// operator picking five tools does not produce five half-applied states on the server — and can
/// change their mind without having granted anything.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>The whole connected catalogue, as the server described it.</summary>
    private List<(string Name, string Server, string Description)> _catalogue = [];

    /// <summary>Grants as last read from the server, by agent.</summary>
    private readonly Dictionary<string, HashSet<string>> _grants = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Unsaved edits for the agent on screen. Null when nothing has been touched.</summary>
    private HashSet<string>? _editing;

    /// <summary>True when the panel holds edits that have not been sent.</summary>
    public bool HasUnsavedGrants => _editing is not null;

    /// <summary>
    /// The catalogue arrived. An empty one means the server has no tool backend, which is the
    /// common case — the panel says so rather than showing an empty list that looks broken.
    /// </summary>
    public void SetToolCatalogue(IEnumerable<(string Name, string Server, string Description)> tools)
    {
        _catalogue = [.. tools];
        Model.ToolsButtonClass = _catalogue.Count > 0 ? "rail-button" : "rail-button hidden";
        RebuildToolPanel();
    }

    /// <summary>Grants for one agent, as read from the server. Discards unsaved edits for it.</summary>
    public void SetToolGrants(string agent, IEnumerable<string> tools)
    {
        _grants[agent] = [.. tools];
        if (string.Equals(agent, Model.ToolsAgent, StringComparison.OrdinalIgnoreCase))
        {
            _editing = null;
        }

        RebuildToolPanel();
    }

    /// <summary>Show or hide the panel.</summary>
    public void ShowToolPanel(bool visible)
    {
        Model.ToolsClass = visible ? "toolpanel" : "toolpanel hidden";
        if (!visible)
        {
            // Dropping edits on close is the honest behaviour: nothing was sent, so leaving them
            // to reappear later would suggest a grant that does not exist.
            _editing = null;
            RebuildToolPanel();
        }
    }

    public bool ToolPanelVisible => !Model.ToolsClass.Contains("hidden", StringComparison.Ordinal);

    /// <summary>Edit a different agent's grants. Unsaved edits for the previous one are dropped.</summary>
    public void SelectToolAgent(string agent)
    {
        Model.ToolsAgent = agent;

        // Named only once there is a name; otherwise the heading read "Tools · " with nothing
        // after it, which looks like something failed to load.
        Model.ToolsTitle = agent.Length > 0 ? $"Tools · {agent}" : "Tools";
        _editing = null;
        Model.ToolsStatus = _grants.ContainsKey(agent) ? "" : "Loading grants…";
        RebuildToolPanel();
    }

    /// <summary>
    /// Turn one tool on or off for the selected agent. Local only — <see cref="PendingGrants"/>
    /// is what gets sent.
    /// </summary>
    public void ToggleTool(string tool)
    {
        if (Model.ToolsAgent.Length == 0)
        {
            return;
        }

        _editing ??= [.. Current(Model.ToolsAgent)];
        if (!_editing.Add(tool))
        {
            _editing.Remove(tool);
        }

        Model.ToolsStatus = "Unsaved changes";
        RebuildToolPanel();
    }

    /// <summary>What Save should send: the edits if there are any, otherwise what is already set.</summary>
    public IReadOnlyList<string> PendingGrants =>
        [.. (_editing ?? Current(Model.ToolsAgent)).OrderBy(t => t, StringComparer.Ordinal)];

    /// <summary>The save landed: adopt the edits as the server's state.</summary>
    public void ToolGrantsSaved(string agent, IEnumerable<string> tools)
    {
        _grants[agent] = [.. tools];
        _editing = null;
        Model.ToolsStatus = $"Saved {_grants[agent].Count} tool(s) for {agent}.";
        RebuildToolPanel();
    }

    /// <summary>The save was refused. Edits are kept so the operator does not lose the selection.</summary>
    public void ToolGrantsFailed(string reason) => Model.ToolsStatus = reason;

    private HashSet<string> Current(string agent) =>
        _grants.TryGetValue(agent, out var held) ? held : [];

    private void RebuildToolPanel()
    {
        var shown = _editing ?? Current(Model.ToolsAgent);

        Model.ToolCatalog = _catalogue
            .OrderBy(t => t.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new ToolRow
            {
                Name = t.Name,
                Server = t.Server,
                Description = t.Description,
                Mark = shown.Contains(t.Name) ? "on" : "",
                RowClass = shown.Contains(t.Name) ? "tool granted" : "tool",
            })
            .ToList();

        // Every agent the client has seen anywhere, not just in this room: an operator granting
        // tools is not necessarily standing in the room the agent works in.
        Model.ToolAgents = KnownAgentNicks()
            .Select(nick => new ToolAgentRow
            {
                Nick = nick,
                Summary = Summarise(nick),
                RowClass = string.Equals(nick, Model.ToolsAgent, StringComparison.OrdinalIgnoreCase)
                    ? "tool-agent active"
                    : "tool-agent",
            })
            .ToList();
    }

    private string Summarise(string nick)
    {
        var count = string.Equals(nick, Model.ToolsAgent, StringComparison.OrdinalIgnoreCase) && _editing is not null
            ? _editing.Count
            : _grants.TryGetValue(nick, out var held) ? held.Count : -1;

        // A dash, not "0 of 12": an agent whose grants have not been read yet is not the same as
        // one that holds nothing, and showing them the same way invites granting twice.
        return count < 0 ? "—" : $"{count} of {_catalogue.Count}";
    }

    private IEnumerable<string> KnownAgentNicks() =>
        _agents.Values
            .SelectMany(rows => rows.Select(r => r.Nick))
            .Concat(_grants.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
}
