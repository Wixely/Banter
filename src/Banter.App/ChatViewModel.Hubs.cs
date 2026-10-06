using Banter.Protocol;

namespace Banter.App;

/// <summary>
/// The hubs page: what this server defers to for tools, and who it knows there.
///
/// <para><b>Read-only, on purpose.</b> The hub is the authority and Banter manages it (PLAN §8c):
/// grants live on MCPHub, keyed by its own id for each agent, and this server calls as the agent
/// rather than on its behalf. A page that let an admin edit grants here would be a second place
/// to decide the same thing, and the one further from where it is enforced. What it does instead
/// is answer the three questions the tools panel cannot: can this server reach the hub at all,
/// does the hub let it manage identities, and what does the hub think each agent may call.</para>
///
/// <para>That last one is the point. The tools panel shows what Banter grants; this shows what
/// the hub grants, as the hub reports it. When they disagree, the hub wins — and an operator can
/// only see that they disagree if someone shows both.</para>
/// </summary>
public sealed partial class ChatViewModel
{
    private IReadOnlyList<HubPayload> _hubListing = [];

    public void ShowHubsPanel(bool show)
    {
        if (show)
        {
            OnlyPanel(Panel.Hubs);
        }
        else
        {
            Model.HubsPanelClass = "mgmt hidden";
        }

        ClearHubDetail();
    }

    public bool HubsPanelOpen => !Model.HubsPanelClass.Contains("hidden", StringComparison.Ordinal);

    public void SetHubs(IEnumerable<HubPayload> hubs)
    {
        _hubListing = [.. hubs];
        Model.AdminHubs = [.. _hubListing.Select(h => new AdminHubRow
        {
            HubKey = h.Key,
            Name = h.DisplayName.Length > 0 ? h.DisplayName : h.Key,
            Detail = $"{Count(h.ToolCount, "tool")} · {Count(h.Agents.Count, "agent")}",
            Initials = InitialsOf(h.DisplayName.Length > 0 ? h.DisplayName : h.Key),
            State = Reachability(h),
            // Amber for a hub that is not connected, plain for one that is: the point of the list
            // is to find the one that has stopped answering.
            StateClass = Connected(h) ? "mgmt-state muted" : "mgmt-state pending",
            RowClass = string.Equals(h.Key, Model.HubSelected, StringComparison.Ordinal)
                ? "mgmt-row selected"
                : "mgmt-row",
        })];

        Model.HubsStatus = Model.AdminHubs.Count switch
        {
            0 => "No hubs configured",
            1 => "1 hub",
            var n => $"{n} hubs",
        };

        // The open detail moves with the list, so a hub that drops out takes its pane with it.
        if (Model.HubSelected.Length > 0)
        {
            SelectHub(Model.HubSelected);
        }
    }

    /// <summary>
    /// The server cannot answer. Said on the page rather than in the room, and said as a status
    /// rather than an empty list: "no hubs" and "I could not ask" look identical otherwise, and
    /// only one of them is worth doing something about.
    /// </summary>
    public void HubsUnavailable(string reason)
    {
        _hubListing = [];
        Model.AdminHubs = [];
        Model.HubsStatus = reason;
        ClearHubDetail();
    }

    public void SelectHub(string key)
    {
        var hub = _hubListing.FirstOrDefault(h => string.Equals(h.Key, key, StringComparison.Ordinal));
        if (hub is null)
        {
            ClearHubDetail();
            return;
        }

        Model.HubSelected = hub.Key;
        foreach (var row in Model.AdminHubs)
        {
            row.RowClass = string.Equals(row.HubKey, hub.Key, StringComparison.Ordinal)
                ? "mgmt-row selected"
                : "mgmt-row";
        }

        Model.HubDetailTitle = hub.DisplayName.Length > 0 ? hub.DisplayName : hub.Key;
        // Not "cannot reach it": a hub can be unconnected for four different reasons and only
        // one of them is a failure. What is true of all four is that nothing it holds is usable,
        // and the state line below says which it is.
        Model.HubDetailSubtitle = Connected(hub)
            ? $"Answering as this server's own user, offering {Count(hub.ToolCount, "tool")}."
            : "Not answering this server, so nothing it offers is available to anyone.";

        Model.HubState = Reachability(hub) + (hub.Detail.Length > 0 ? $" — {hub.Detail}" : "");
        Model.HubKey = hub.Key;
        Model.HubTools = Count(hub.ToolCount, "tool");
        Model.HubManagement = hub.Administrable
            ? "Offers its management tools, so identities and grants can be provisioned from here."
            : "Read-only: it does not offer its management tools to the user this server connects "
              + "as, so nothing about identities or grants can be changed from here.";

        Model.HubAgents = [.. hub.Agents
            .OrderBy(a => a.Agent, StringComparer.Ordinal)
            .Select(a => new HubAgentRow
            {
                Agent = a.Agent,
                Initials = InitialsOf(a.Agent),
                UserId = a.UserId,
                Issued = $"key issued {Moment(a.IssuedAtUnixMs)}",
                Tools = a.Tools.Count == 0
                    ? "nothing granted there yet"
                    : string.Join(", ", a.Tools.Order(StringComparer.Ordinal)),
            })];

        Model.HubAgentsClass = Model.HubAgents.Count > 0 ? "mgmt-field" : "mgmt-field hidden";

        Model.HubDetailClass = "mgmt-detail";
        Model.HubEmptyClass = "mgmt-empty hidden";
    }

    public void ClearHubDetail()
    {
        Model.HubSelected = "";
        foreach (var row in Model.AdminHubs)
        {
            row.RowClass = "mgmt-row";
        }

        Model.HubDetailClass = "mgmt-detail hidden";
        Model.HubEmptyClass = "mgmt-empty";
        Model.HubDetailTitle = "";
        Model.HubDetailSubtitle = "";
        Model.HubAgents = [];
        Model.HubAgentsClass = "mgmt-field hidden";
    }

    /// <summary>
    /// Compared case-insensitively because the word comes from MCPHub's own enum over the wire,
    /// and this page must not start lying if that enum is ever spelled differently.
    /// </summary>
    private static bool Connected(HubPayload hub) =>
        string.Equals(hub.State, "Connected", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The upstream's state in the page's voice. The proxy's own words carry through -
    /// connecting, disconnected, faulted - because they mean different things and an operator
    /// chasing a missing tool needs to know which.
    ///
    /// <para>"Unknown" is the exception, and is not the proxy's word at all: it is what the broker
    /// says when the registry has no entry for an upstream, which means this server never dialled
    /// it. Said plainly, because "unknown" sounds like a fault and is not one.</para>
    /// </summary>
    private static string Reachability(HubPayload hub) => hub.State.ToLowerInvariant() switch
    {
        "connected" => "connected",
        "unknown" or "" => "not started - this server has not tried to connect",
        var other => other,
    };

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Moment(long unixMilliseconds) => unixMilliseconds <= 0
        ? "at some point before this server was keeping track"
        : DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToLocalTime().ToString("d MMM, HH:mm");
}
