using Banter.Protocol;

namespace Banter.App;

/// <summary>
/// The agents page: who the agents are, and the one-time code that lets a machine become one.
///
/// <para>Everything here is an operator act on a running server — creating, editing and removing
/// take effect immediately, because the server is the authority and is asked every time. There is
/// no config file to edit and nothing to restart.</para>
///
/// <para><b>No private key ever passes through here.</b> The page mints an enrolment code; the
/// agent's machine makes its own key when it redeems one. That is why the code is the only secret
/// on this screen, and why it is worth showing once and loudly rather than storing.</para>
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>Show or hide the page. Only an admin ever sees the button that opens it.</summary>
    public void ShowAdminPanel(bool show)
    {
        Model.AdminClass = show ? "adminpanel" : "adminpanel hidden";
        if (!show)
        {
            // A code left on screen after the page closes is a secret nobody is watching.
            ClearAdminCode();
        }
    }

    public bool AdminPanelOpen => !Model.AdminClass.Contains("hidden", StringComparison.Ordinal);

    /// <summary>The button appears only for an admin — for anyone else the verbs would be refused.</summary>
    public void SetIsAdmin(bool isAdmin) =>
        Model.AdminButtonClass = isAdmin ? "admin-open" : "admin-open hidden";

    /// <summary>The identities, as the server lists them.</summary>
    public void SetAgentIdentities(IEnumerable<AgentIdentityPayload> identities)
    {
        Model.AdminAgents = [.. identities.Select(i => new AdminAgentRow
        {
            Nick = i.Nick,
            Initials = InitialsOf(i.Nick),
            Detail = $"{i.Locality} · {i.Clearance} · {string.Join(", ", i.Rooms)}",

            // What an operator most needs to know at a glance is whether this identity is actually
            // usable, and if so which machine holds it.
            State = i.Enrolled ? i.KeyFingerprint
                : i.EnrolmentPending ? "waiting for a machine to enrol"
                : "no key and no code — reissue to give it one",
            StateClass = i.Enrolled ? "admin-state" : "admin-state pending",
            RowClass = string.Equals(i.Nick, Model.AdminSelected, StringComparison.OrdinalIgnoreCase)
                ? "admin-agent selected"
                : "admin-agent",
        })];

        Model.AdminStatus = Model.AdminAgents.Count == 0
            ? "No agents yet. Add one below."
            : $"{Model.AdminAgents.Count} agent{(Model.AdminAgents.Count == 1 ? "" : "s")}";
    }

    /// <summary>Selects an identity, so the edit controls act on it.</summary>
    public void SelectAdminAgent(string nick)
    {
        Model.AdminSelected = nick;
        foreach (var row in Model.AdminAgents)
        {
            row.RowClass = string.Equals(row.Nick, nick, StringComparison.OrdinalIgnoreCase)
                ? "admin-agent selected"
                : "admin-agent";
        }
    }

    /// <summary>
    /// Shows a freshly minted code. It is displayed once and never stored: the server keeps only a
    /// hash, so this is the only moment it exists anywhere an operator can read it.
    /// </summary>
    public void ShowEnrolmentCode(string nick, string code)
    {
        Model.AdminCode = code;
        Model.AdminCodeClass = "admin-code";
        Model.AdminCodeFor = $"Paste this into the machine that will run {nick}, within the hour. It works once.";
    }

    public void ClearAdminCode()
    {
        Model.AdminCode = "";
        Model.AdminCodeClass = "admin-code hidden";
        Model.AdminCodeFor = "";
    }

    public void AdminFailed(string message)
    {
        Model.AdminStatus = message;
        ClearAdminCode();
    }

    /// <summary>Empties the add form, after it has been used.</summary>
    public void ClearNewAgent()
    {
        Model.NewAgentNick = "";
        Model.NewAgentRooms = "";
        Model.NewAgentSkills = "";
    }

    /// <summary>
    /// Cycles local → frontier. Two values, so a toggle beats a dropdown — and it is the field that
    /// decides whether anything said in the room may leave, so it is worth being blunt about.
    /// </summary>
    public void CycleNewAgentLocality() =>
        Model.NewAgentLocality = Model.NewAgentLocality == "local" ? "frontier" : "local";

    /// <summary>Cycles public → internal → sensitive.</summary>
    public void CycleNewAgentClearance() =>
        Model.NewAgentClearance = Model.NewAgentClearance switch
        {
            "public" => "internal",
            "internal" => "sensitive",
            _ => "public",
        };

    /// <summary>
    /// The add form's contents, or null when it is not filled in enough to send. Rooms and skills
    /// fall back to something sensible rather than refusing: an agent with no room is in nothing,
    /// which is never what somebody meant.
    /// </summary>
    public (string Nick, string[] Rooms, string[] Skills, AgentLocality Locality, DataSensitivity Clearance)? ReadNewAgent()
    {
        var nick = Model.NewAgentNick.Trim();
        if (nick.Length == 0)
        {
            Model.AdminStatus = "Give the agent a name first.";
            return null;
        }

        var rooms = Split(Model.NewAgentRooms);
        var skills = Split(Model.NewAgentSkills);

        return (
            nick,
            rooms.Length > 0 ? rooms : [Model.ActiveRoom.Length > 0 ? Model.ActiveRoom : "#main"],
            skills.Length > 0 ? skills : ["chat"],
            Model.NewAgentLocality == "frontier" ? AgentLocality.Frontier : AgentLocality.Local,
            Model.NewAgentClearance switch
            {
                "public" => DataSensitivity.Public,
                "internal" => DataSensitivity.Internal,
                _ => DataSensitivity.Sensitive,
            });
    }

    private static string[] Split(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
