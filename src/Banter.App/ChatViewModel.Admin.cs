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
        if (show)
        {
            // Always opens on agents — the tab you were on last time is not a preference worth
            // keeping, and a stale one would show a list that has not been loaded yet.
            ShowAdminTab(users: false);
        }
        else
        {
            // A code left on screen after the page closes is a secret nobody is watching.
            ClearAdminCode();
        }
    }

    /// <summary>Switches between the agents tab and the users tab. Either way the one-secret
    /// banner is cleared: a code shown for one tab is noise — or worse — on the other.</summary>
    public void ShowAdminTab(bool users)
    {
        Model.AdminTabAgentsClass = users ? "admin-tab" : "admin-tab selected";
        Model.AdminTabUsersClass = users ? "admin-tab selected" : "admin-tab";
        Model.AdminAgentsViewClass = users ? "adminpanel-body hidden" : "adminpanel-body";
        Model.AdminUsersViewClass = users ? "adminpanel-body" : "adminpanel-body hidden";
        ClearAdminCode();
    }

    public bool AdminPanelOpen => !Model.AdminClass.Contains("hidden", StringComparison.Ordinal);

    /// <summary>The button appears only for an admin — for anyone else the verbs would be refused.</summary>
    public void SetIsAdmin(bool isAdmin) =>
        Model.AdminButtonClass = isAdmin ? "admin-open" : "admin-open hidden";

    /// <summary>The raw listing, kept beside the rows so selection can read the overrides.</summary>
    private IReadOnlyList<AgentIdentityPayload> _identityListing = [];

    /// <summary>The identities, as the server lists them.</summary>
    public void SetAgentIdentities(IEnumerable<AgentIdentityPayload> identities)
    {
        _identityListing = [.. identities];
        Model.AdminAgents = [.. _identityListing.Select(i => new AdminAgentRow
        {
            Nick = i.Nick,
            Initials = InitialsOf(i.Nick),
            Detail = $"{i.Locality} · {i.Clearance} · {string.Join(", ", i.Rooms)}"
                + (i.CostTier is { } cost ? $" · cost {cost}" : "")
                + (i.WantsDelegator is { } wants ? (wants ? " · delegator pinned" : " · delegator never") : ""),

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

        // The override controls show the selected identity's standing state, so "Apply" writes
        // exactly what the panel says rather than a diff against something invisible.
        var identity = _identityListing.FirstOrDefault(i =>
            string.Equals(i.Nick, nick, StringComparison.OrdinalIgnoreCase));
        Model.AdminCostOverride = identity?.CostTier?.ToString() ?? "";
        Model.AdminDelegatorLabel = DelegatorLabel(identity?.WantsDelegator);
    }

    private static string DelegatorLabel(bool? wants) => wants switch
    {
        true => "Delegator: pinned",
        false => "Delegator: never",
        null => "Delegator: agent decides",
    };

    /// <summary>Cycles agent decides → pinned → never. Three states, so a cycle beats a dropdown.</summary>
    public void CycleAdminDelegator() =>
        Model.AdminDelegatorLabel = Model.AdminDelegatorLabel switch
        {
            "Delegator: agent decides" => DelegatorLabel(true),
            "Delegator: pinned" => DelegatorLabel(false),
            _ => DelegatorLabel(null),
        };

    /// <summary>
    /// The override controls as absolute state to write, or null when they cannot be read — a
    /// cost that is not a number is refused here, before it becomes a request.
    /// </summary>
    public (int? CostTier, bool? WantsDelegator)? ReadAgentOverrides()
    {
        var costText = Model.AdminCostOverride.Trim();
        int? cost = null;
        if (costText.Length > 0)
        {
            if (!int.TryParse(costText, out var parsed) || parsed < 0)
            {
                Model.AdminStatus = "Cost must be a number, or empty for the agent to decide.";
                return null;
            }

            cost = parsed;
        }

        bool? wants = Model.AdminDelegatorLabel switch
        {
            "Delegator: pinned" => true,
            "Delegator: never" => false,
            _ => null,
        };

        return (cost, wants);
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

    // ---- The users tab ----

    /// <summary>The accounts, as the server lists them.</summary>
    public void SetUsers(IEnumerable<UserAccountPayload> users)
    {
        Model.AdminUsers = [.. users.Select(u => new AdminUserRow
        {
            Username = u.Username,
            Initials = InitialsOf(u.Username),
            Detail = u.IsAdmin ? "admin" : "member",
            IsAdmin = u.IsAdmin,
            RowClass = string.Equals(u.Username, Model.AdminUserSelected, StringComparison.OrdinalIgnoreCase)
                ? "admin-agent selected"
                : "admin-agent",
        })];

        Model.AdminStatus = $"{Model.AdminUsers.Count} user{(Model.AdminUsers.Count == 1 ? "" : "s")}";
        RefreshUserToggleLabel();
    }

    /// <summary>Selects a user, so the edit controls act on them.</summary>
    public void SelectAdminUser(string username)
    {
        Model.AdminUserSelected = username;
        foreach (var row in Model.AdminUsers)
        {
            row.RowClass = string.Equals(row.Username, username, StringComparison.OrdinalIgnoreCase)
                ? "admin-agent selected"
                : "admin-agent";
        }

        RefreshUserToggleLabel();
    }

    /// <summary>The selected user's row, or null — selection can outlive a refresh that removed them.</summary>
    public AdminUserRow? SelectedUser =>
        Model.AdminUsers.FirstOrDefault(r =>
            string.Equals(r.Username, Model.AdminUserSelected, StringComparison.OrdinalIgnoreCase));

    /// <summary>The one toggle button reads as the action it would take, not the state it sees.</summary>
    private void RefreshUserToggleLabel() =>
        Model.AdminUserToggleLabel = SelectedUser is { IsAdmin: true } ? "Make member" : "Make admin";

    /// <summary>
    /// Shows a freshly minted temporary password, in the same banner the enrolment code uses:
    /// displayed once, never stored, gone when the page closes or the tab changes.
    /// </summary>
    public void ShowTempPassword(string username, string password)
    {
        Model.AdminCode = password;
        Model.AdminCodeClass = "admin-code";
        Model.AdminCodeFor = $"Hand this to {username}, once. They should change it when they first sign in.";
    }

    /// <summary>Cycles member → admin. Two values, so a toggle beats a dropdown.</summary>
    public void CycleNewUserRole() =>
        Model.NewUserRole = Model.NewUserRole == "member" ? "admin" : "member";

    /// <summary>The add form's contents, or null when there is no name to send.</summary>
    public (string Username, bool IsAdmin)? ReadNewUser()
    {
        var username = Model.NewUserName.Trim();
        if (username.Length == 0)
        {
            Model.AdminStatus = "Give the user a name first.";
            return null;
        }

        return (username, Model.NewUserRole == "admin");
    }

    /// <summary>Empties the add form, after it has been used.</summary>
    public void ClearNewUser()
    {
        Model.NewUserName = "";
        Model.NewUserRole = "member";
    }
}
