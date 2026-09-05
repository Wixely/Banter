using Banter.Protocol;

namespace Banter.App;

/// <summary>
/// The two management pages — agents and users — as one shape: a list on the left, a form for
/// whatever is selected on the right, one footer.
///
/// <para>The form is always in one of three states, and says which: nothing chosen, creating, or
/// editing a named thing. That is the whole reason this was rewritten — the page used to show an
/// "add" form and an "act on the selection" column at the same time, so the answer to "what will
/// this button do" depended on which half you had last touched.</para>
///
/// <para><b>No private key ever passes through here.</b> The agents page mints a one-time code and
/// the agent's machine makes its own key; the users page hands out a temporary password the server
/// invented. Both are shown once, in the same banner, and neither is stored.</para>
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>Which of the three states a detail pane is in.</summary>
    private enum DetailMode
    {
        None,
        New,
        Edit,
    }

    private DetailMode _agentMode = DetailMode.None;
    private DetailMode _userMode = DetailMode.None;

    // ── Opening and closing ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Show or hide the agents page. Agents and users are separate pages rather than tabs in one:
    /// they are separate jobs — who may run in a room versus who may sign in — and each has its
    /// own way into the rail. Opening either closes the other, because the rail is a place you
    /// are, not a set of things you have open.
    /// </summary>
    public void ShowAgentsPanel(bool show)
    {
        Model.AgentsPanelClass = show ? "mgmt" : "mgmt hidden";
        if (show)
        {
            Model.UsersPanelClass = "mgmt hidden";
        }

        // Leaving a page drops what was on it: a half-typed form is not worth restoring, and a
        // secret left on screen is one nobody is watching.
        ClearAgentDetail();
        ClearAdminCode();
    }

    public void ShowUsersPanel(bool show)
    {
        Model.UsersPanelClass = show ? "mgmt" : "mgmt hidden";
        if (show)
        {
            Model.AgentsPanelClass = "mgmt hidden";
        }

        ClearUserDetail();
        ClearAdminCode();
    }

    public bool AgentsPanelOpen => !Model.AgentsPanelClass.Contains("hidden", StringComparison.Ordinal);

    public bool UsersPanelOpen => !Model.UsersPanelClass.Contains("hidden", StringComparison.Ordinal);

    /// <summary>Both buttons appear only for an admin — for anyone else the verbs would be refused.</summary>
    public void SetIsAdmin(bool isAdmin)
    {
        Model.AgentsButtonClass = isAdmin ? "rail-button" : "rail-button hidden";
        Model.UsersButtonClass = isAdmin ? "rail-button" : "rail-button hidden";
    }

    // ── The one-shot secret banner ───────────────────────────────────────────────────────────

    /// <summary>
    /// Shows a freshly minted enrolment code. It is displayed once and never stored: the server
    /// keeps only a hash, so this is the only moment it exists anywhere an operator can read it.
    /// </summary>
    public void ShowEnrolmentCode(string nick, string code)
    {
        Model.AdminCode = code;
        Model.AdminCodeClass = "mgmt-secret";
        Model.AdminCodeFor = $"Redeem this where {nick} will run, within the hour. It works once.";
    }

    /// <summary>The same banner for a temporary password — same rules, same one chance to read it.</summary>
    public void ShowTempPassword(string username, string password)
    {
        Model.AdminCode = password;
        Model.AdminCodeClass = "mgmt-secret";
        Model.AdminCodeFor = $"Hand this to {username}, once. They should change it when they first sign in.";
    }

    public void ClearAdminCode()
    {
        Model.AdminCode = "";
        Model.AdminCodeClass = "mgmt-secret hidden";
        Model.AdminCodeFor = "";
    }

    public void AdminFailed(string message)
    {
        Model.AgentsStatus = message;
        Model.UsersStatus = message;
        ClearAdminCode();
    }

    // ── Agents: the list ─────────────────────────────────────────────────────────────────────

    /// <summary>The raw listing, kept beside the rows so the form can be filled from it.</summary>
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
            // usable, and if so which key is answering for it.
            State = i.Enrolled ? i.KeyFingerprint
                : i.EnrolmentPending ? "waiting for a machine to enrol"
                : "no key and no code — reissue to give it one",
            StateClass = i.Enrolled ? "mgmt-state" : "mgmt-state pending",
            RowClass = RowClassFor(i.Nick, Model.AgentSelected),
        })];

        Model.AgentsStatus = Model.AdminAgents.Count switch
        {
            0 => "No agents yet",
            1 => "1 agent",
            var n => $"{n} agents",
        };

        // A refresh must not strand the form on something that no longer exists.
        if (_agentMode == DetailMode.Edit
            && !_identityListing.Any(i => string.Equals(i.Nick, Model.AgentSelected, StringComparison.OrdinalIgnoreCase)))
        {
            ClearAgentDetail();
        }
    }

    // ── Agents: the detail pane ──────────────────────────────────────────────────────────────

    /// <summary>Begins creating an agent: an empty form that says it is creating one.</summary>
    public void NewAgent()
    {
        _agentMode = DetailMode.New;
        Model.AgentSelected = "";
        foreach (var row in Model.AdminAgents)
        {
            row.RowClass = "mgmt-row";
        }

        Model.AgentFormNick = "";
        Model.AgentFormRooms = Model.ActiveRoom.Length > 0 ? Model.ActiveRoom : "#main";
        Model.AgentFormSkills = "chat";
        Model.AgentFormCost = "";
        Model.AgentLocalityChoices = LocalityChoices("local");
        Model.AgentClearanceChoices = ClearanceChoices("sensitive");
        Model.AgentDelegatorChoices = DelegatorChoices("auto");
        Model.AgentWorkModeChoices = WorkModeChoices("auto");

        Model.AgentDetailTitle = "New agent";
        Model.AgentDetailSubtitle = "Create an identity, then enrol it where the agent will run.";
        Model.AgentSaveLabel = "Create agent";
        Model.AgentDetailClass = "mgmt-detail";
        Model.AgentEmptyClass = "mgmt-empty hidden";
        Model.AgentRemoveClass = "mgmt-remove hidden";
        Model.AgentReissueClass = "mgmt-inline hidden";
        Model.AgentKeyFieldClass = "mgmt-field hidden";
        Model.AgentNickFieldClass = "mgmt-field";
        Model.AgentFormNickReadonly = "";
        ClearAdminCode();
        MarkAgentDirty();
    }

    /// <summary>Selects an identity and fills the form from it.</summary>
    public void SelectAdminAgent(string nick)
    {
        var identity = _identityListing.FirstOrDefault(i =>
            string.Equals(i.Nick, nick, StringComparison.OrdinalIgnoreCase));
        if (identity is null)
        {
            ClearAgentDetail();
            return;
        }

        _agentMode = DetailMode.Edit;
        Model.AgentSelected = identity.Nick;
        foreach (var row in Model.AdminAgents)
        {
            row.RowClass = RowClassFor(row.Nick, identity.Nick);
        }

        Model.AgentFormNick = identity.Nick;
        Model.AgentFormNickReadonly = identity.Nick;
        Model.AgentFormRooms = string.Join(", ", identity.Rooms);
        Model.AgentFormSkills = string.Join(", ", identity.Skills);
        Model.AgentFormCost = identity.CostTier?.ToString() ?? "";
        Model.AgentLocalityChoices = LocalityChoices(identity.Locality);
        Model.AgentClearanceChoices = ClearanceChoices(identity.Clearance);
        Model.AgentDelegatorChoices = DelegatorChoices(
            identity.WantsDelegator switch { true => "always", false => "never", null => "auto" });
        Model.AgentWorkModeChoices = WorkModeChoices(identity.WorkMode ?? "auto");
        Model.AgentFingerprint = identity.Enrolled ? identity.KeyFingerprint : "not enrolled yet";

        Model.AgentDetailTitle = identity.Nick;
        Model.AgentDetailSubtitle = "Update the settings for this agent.";
        Model.AgentSaveLabel = "Save changes";
        Model.AgentDetailClass = "mgmt-detail";
        Model.AgentEmptyClass = "mgmt-empty hidden";
        Model.AgentRemoveClass = "mgmt-remove";
        Model.AgentReissueClass = "mgmt-inline";
        Model.AgentKeyFieldClass = "mgmt-field";
        // The nick is the identity. Changing it would be creating a different agent, so the field
        // shows it and refuses it rather than pretending a rename is on offer.
        Model.AgentNickFieldClass = "mgmt-field readonly";
        ClearAdminCode();
        _agentBaseline = ReadAgentFormRaw();
        MarkAgentClean();
    }

    /// <summary>Back to "nothing chosen" — the state the page opens in.</summary>
    public void ClearAgentDetail()
    {
        _agentMode = DetailMode.None;
        Model.AgentSelected = "";
        foreach (var row in Model.AdminAgents)
        {
            row.RowClass = "mgmt-row";
        }

        Model.AgentDetailClass = "mgmt-detail hidden";
        Model.AgentEmptyClass = "mgmt-empty";
        Model.AgentDirtyClass = "mgmt-dirty hidden";

        // The head is always on screen now, so it must not keep announcing whatever was last
        // selected. Only the close control is left when nothing is.
        Model.AgentDetailTitle = "";
        Model.AgentDetailSubtitle = "";
        Model.AgentRemoveClass = "mgmt-remove hidden";
    }

    public bool AgentIsNew => _agentMode == DetailMode.New;

    public bool AgentDetailOpen => _agentMode != DetailMode.None;

    public void ChooseAgentLocality(string value) =>
        Apply(() => Model.AgentLocalityChoices = LocalityChoices(value));

    public void ChooseAgentClearance(string value) =>
        Apply(() => Model.AgentClearanceChoices = ClearanceChoices(value));

    public void ChooseAgentDelegator(string value) =>
        Apply(() => Model.AgentDelegatorChoices = DelegatorChoices(value));

    public void ChooseAgentWorkMode(string value) =>
        Apply(() => Model.AgentWorkModeChoices = WorkModeChoices(value));

    private void Apply(Action change)
    {
        change();
        RefreshAgentDirty();
    }

    /// <summary>Re-checks the form against what was loaded, so the footer only claims unsaved
    /// changes when something actually differs.</summary>
    public void RefreshAgentDirty()
    {
        if (_agentMode == DetailMode.New)
        {
            MarkAgentDirty();
            return;
        }

        if (_agentMode == DetailMode.None || ReadAgentFormRaw() == _agentBaseline)
        {
            MarkAgentClean();
        }
        else
        {
            MarkAgentDirty();
        }
    }

    private string _agentBaseline = "";

    private string ReadAgentFormRaw() => string.Join('', [
        Model.AgentFormRooms.Trim(),
        Model.AgentFormSkills.Trim(),
        Model.AgentFormCost.Trim(),
        Chosen(Model.AgentLocalityChoices),
        Chosen(Model.AgentClearanceChoices),
        Chosen(Model.AgentDelegatorChoices),
        Chosen(Model.AgentWorkModeChoices),
    ]);

    private void MarkAgentDirty() => Model.AgentDirtyClass = "mgmt-dirty";

    private void MarkAgentClean() => Model.AgentDirtyClass = "mgmt-dirty hidden";

    /// <summary>
    /// The form as something to send, or null when it cannot be read. Everything is validated
    /// here — before it becomes a request — so a refusal reads as the field it came from.
    /// </summary>
    public AgentForm? ReadAgentForm()
    {
        var nick = (_agentMode == DetailMode.New ? Model.AgentFormNick : Model.AgentSelected).Trim();
        if (nick.Length == 0)
        {
            Model.AgentsStatus = "Give the agent a name first.";
            return null;
        }

        int? cost = null;
        var costText = Model.AgentFormCost.Trim();
        if (costText.Length > 0)
        {
            if (!int.TryParse(costText, out var parsed) || parsed < 0)
            {
                Model.AgentsStatus = "Cost must be a whole number, or empty to let the agent decide.";
                return null;
            }

            cost = parsed;
        }

        var rooms = Split(Model.AgentFormRooms);
        var skills = Split(Model.AgentFormSkills);

        return new AgentForm(
            nick,
            rooms.Length > 0 ? rooms : [Model.ActiveRoom.Length > 0 ? Model.ActiveRoom : "#main"],
            skills.Length > 0 ? skills : ["chat"],
            Chosen(Model.AgentLocalityChoices) == "frontier" ? AgentLocality.Frontier : AgentLocality.Local,
            Chosen(Model.AgentClearanceChoices) switch
            {
                "public" => DataSensitivity.Public,
                "internal" => DataSensitivity.Internal,
                _ => DataSensitivity.Sensitive,
            },
            cost,
            Chosen(Model.AgentDelegatorChoices) switch { "always" => true, "never" => false, _ => null },
            Chosen(Model.AgentWorkModeChoices) switch
            {
                "delegateonly" => AgentWorkMode.DelegateOnly,
                "workwhenalone" => AgentWorkMode.WorkWhenAlone,
                "delegateandwork" => AgentWorkMode.DelegateAndWork,
                _ => null,
            });
    }

    // ── Users: the list ──────────────────────────────────────────────────────────────────────

    private IReadOnlyList<UserAccountPayload> _userListing = [];

    /// <summary>The accounts, as the server lists them.</summary>
    public void SetUsers(IEnumerable<UserAccountPayload> users)
    {
        _userListing = [.. users];
        Model.AdminUsers = [.. _userListing.Select(u => new AdminUserRow
        {
            Username = u.Username,
            Initials = InitialsOf(u.Username),
            Detail = u.IsAdmin ? "admin" : "member",
            IsAdmin = u.IsAdmin,
            RowClass = RowClassFor(u.Username, Model.UserSelected),
        })];

        Model.UsersStatus = Model.AdminUsers.Count switch
        {
            0 => "No users yet",
            1 => "1 user",
            var n => $"{n} users",
        };

        if (_userMode == DetailMode.Edit
            && !_userListing.Any(u => string.Equals(u.Username, Model.UserSelected, StringComparison.OrdinalIgnoreCase)))
        {
            ClearUserDetail();
        }
    }

    // ── Users: the detail pane ───────────────────────────────────────────────────────────────

    public void NewUser()
    {
        _userMode = DetailMode.New;
        Model.UserSelected = "";
        foreach (var row in Model.AdminUsers)
        {
            row.RowClass = "mgmt-row";
        }

        Model.UserFormName = "";
        Model.UserRoleChoices = RoleChoices("member");

        Model.UserDetailTitle = "New user";
        Model.UserDetailSubtitle = "Create an account and hand over its first password.";
        Model.UserSaveLabel = "Create user";
        Model.UserDetailClass = "mgmt-detail";
        Model.UserEmptyClass = "mgmt-empty hidden";
        Model.UserRemoveClass = "mgmt-remove hidden";
        Model.UserResetClass = "mgmt-inline hidden";
        Model.UserNickFieldClass = "mgmt-field";
        Model.UserFormNameReadonly = "";
        ClearAdminCode();
        MarkUserDirty();
    }

    public void SelectAdminUser(string username)
    {
        var account = _userListing.FirstOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            ClearUserDetail();
            return;
        }

        _userMode = DetailMode.Edit;
        Model.UserSelected = account.Username;
        foreach (var row in Model.AdminUsers)
        {
            row.RowClass = RowClassFor(row.Username, account.Username);
        }

        Model.UserFormName = account.Username;
        Model.UserFormNameReadonly = account.Username;
        Model.UserRoleChoices = RoleChoices(account.IsAdmin ? "admin" : "member");

        Model.UserDetailTitle = account.Username;
        Model.UserDetailSubtitle = "Update this account, or hand out a new password.";
        Model.UserSaveLabel = "Save changes";
        Model.UserDetailClass = "mgmt-detail";
        Model.UserEmptyClass = "mgmt-empty hidden";
        Model.UserRemoveClass = "mgmt-remove";
        Model.UserResetClass = "mgmt-inline";
        Model.UserNickFieldClass = "mgmt-field readonly";
        ClearAdminCode();
        _userBaseline = Chosen(Model.UserRoleChoices);
        MarkUserClean();
    }

    public void ClearUserDetail()
    {
        _userMode = DetailMode.None;
        Model.UserSelected = "";
        foreach (var row in Model.AdminUsers)
        {
            row.RowClass = "mgmt-row";
        }

        Model.UserDetailClass = "mgmt-detail hidden";
        Model.UserEmptyClass = "mgmt-empty";
        Model.UserDirtyClass = "mgmt-dirty hidden";

        // The head is always on screen now, so it must not keep announcing whatever was last
        // selected. Only the close control is left when nothing is.
        Model.UserDetailTitle = "";
        Model.UserDetailSubtitle = "";
        Model.UserRemoveClass = "mgmt-remove hidden";
    }

    public bool UserIsNew => _userMode == DetailMode.New;

    public bool UserDetailOpen => _userMode != DetailMode.None;

    public void ChooseUserRole(string value)
    {
        Model.UserRoleChoices = RoleChoices(value);
        RefreshUserDirty();
    }

    public void RefreshUserDirty()
    {
        if (_userMode == DetailMode.New)
        {
            MarkUserDirty();
        }
        else if (_userMode == DetailMode.None || Chosen(Model.UserRoleChoices) == _userBaseline)
        {
            MarkUserClean();
        }
        else
        {
            MarkUserDirty();
        }
    }

    private string _userBaseline = "";

    private void MarkUserDirty() => Model.UserDirtyClass = "mgmt-dirty";

    private void MarkUserClean() => Model.UserDirtyClass = "mgmt-dirty hidden";

    /// <summary>The user form as something to send, or null when it cannot be read.</summary>
    public (string Username, bool IsAdmin)? ReadUserForm()
    {
        var name = (_userMode == DetailMode.New ? Model.UserFormName : Model.UserSelected).Trim();
        if (name.Length == 0)
        {
            Model.UsersStatus = "Give the user a name first.";
            return null;
        }

        return (name, Chosen(Model.UserRoleChoices) == "admin");
    }

    // ── Confirming something destructive ─────────────────────────────────────────────────────

    /// <summary>What the confirmation dialog is currently asking about, or none.</summary>
    private enum PendingAct
    {
        None,
        RemoveAgent,
        RemoveUser,
    }

    private PendingAct _pendingAct = PendingAct.None;

    /// <summary>
    /// Asks before removing the selected agent. Removal is instant and total on the server — the
    /// key stops working on the next thing the agent tries — so it is worth one deliberate step,
    /// and the dialog names the subject rather than saying "this item".
    /// </summary>
    public void ConfirmRemoveAgent()
    {
        if (Model.AgentSelected.Length == 0)
        {
            return;
        }

        _pendingAct = PendingAct.RemoveAgent;
        Model.ConfirmTitle = $"Remove {Model.AgentSelected}?";
        Model.ConfirmBody = "Its key stops working immediately, and any session it is holding open "
            + "ends. Enrolling it again means a new code and a new key.";
        Model.ConfirmAction = "Remove agent";
        Model.ConfirmClass = "confirm";
    }

    public void ConfirmRemoveUser()
    {
        if (Model.UserSelected.Length == 0)
        {
            return;
        }

        _pendingAct = PendingAct.RemoveUser;
        Model.ConfirmTitle = $"Remove {Model.UserSelected}?";
        Model.ConfirmBody = "Their password stops working immediately and they are signed out. "
            + "The account cannot be restored — it would have to be created again.";
        Model.ConfirmAction = "Remove user";
        Model.ConfirmClass = "confirm";
    }

    public void CancelConfirm()
    {
        _pendingAct = PendingAct.None;
        Model.ConfirmClass = "confirm hidden";
    }

    public bool ConfirmOpen => !Model.ConfirmClass.Contains("hidden", StringComparison.Ordinal);

    /// <summary>What was confirmed, and the subject it applies to. Clears the dialog either way,
    /// so a second click cannot run the same removal twice.</summary>
    public (bool IsAgent, string Subject)? TakeConfirmed()
    {
        var act = _pendingAct;
        var subject = act == PendingAct.RemoveAgent ? Model.AgentSelected : Model.UserSelected;
        CancelConfirm();
        return act switch
        {
            PendingAct.RemoveAgent => (true, subject),
            PendingAct.RemoveUser => (false, subject),
            _ => null,
        };
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The steps offered on the settings page. Presets rather than a free number because the
    /// useful range is small and every value in it should be one the layout was looked at in.
    /// </summary>
    public static IReadOnlyList<float> ZoomSteps { get; } = [0.75f, 0.9f, 1f, 1.15f, 1.35f, 1.6f, 2f];

    public void ShowSettingsPanel(bool show)
    {
        Model.SettingsPanelClass = show ? "mgmt" : "mgmt hidden";
        if (show)
        {
            Model.AgentsPanelClass = "mgmt hidden";
            Model.UsersPanelClass = "mgmt hidden";
        }
    }

    public bool SettingsPanelOpen => !Model.SettingsPanelClass.Contains("hidden", StringComparison.Ordinal);

    /// <summary>Reflects the zoom actually in force. Called after the document has been told, so
    /// the page shows what happened rather than what was asked for.</summary>
    public void SetZoom(float zoom)
    {
        Model.ZoomLabel = $"{Math.Round(zoom * 100)}%";
        Model.ZoomChoices = [.. ZoomSteps.Select(z => new ChoiceRow
        {
            Value = z.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
            Label = $"{Math.Round(z * 100)}%",
            Hint = "",
            RowClass = Math.Abs(z - zoom) < 0.001f ? "mgmt-choice compact selected" : "mgmt-choice compact",
            DotClass = Math.Abs(z - zoom) < 0.001f ? "mgmt-dot on" : "mgmt-dot",
        })];
    }

    // ── Choice groups ────────────────────────────────────────────────────────────────────────

    private static List<ChoiceRow> Choices(string selected, params (string Value, string Label, string Hint)[] options) =>
        [.. options.Select(o => new ChoiceRow
        {
            Value = o.Value,
            Label = o.Label,
            Hint = o.Hint,
            RowClass = o.Value == selected ? "mgmt-choice selected" : "mgmt-choice",
            DotClass = o.Value == selected ? "mgmt-dot on" : "mgmt-dot",
        })];

    private static List<ChoiceRow> LocalityChoices(AgentLocality locality) =>
        LocalityChoices(locality.ToString().ToLowerInvariant());

    private static List<ChoiceRow> LocalityChoices(string selected) => Choices(selected,
        ("local", "Local", "Runs on a model you host. Nothing it is shown leaves."),
        ("frontier", "Frontier", "Runs on somebody else's model. Anything it is shown leaves."));

    private static List<ChoiceRow> ClearanceChoices(DataSensitivity clearance) =>
        ClearanceChoices(clearance.ToString().ToLowerInvariant());

    private static List<ChoiceRow> ClearanceChoices(string selected) => Choices(selected,
        ("public", "Public", "Only material that could be posted anywhere."),
        ("internal", "Internal", "Ordinary work, not for outside eyes."),
        ("sensitive", "Sensitive", "Everything, including what must stay in the room."));

    private static List<ChoiceRow> DelegatorChoices(string selected) => Choices(selected,
        ("auto", "Agent decides", "Whatever the agent asks for when it announces."),
        ("always", "Always", "Pinned as delegator wherever it is eligible."),
        ("never", "Never", "Never the configured delegator, whatever it asks."));

    /// <summary>
    /// What a delegator does with work nobody else can take. Worth an operator's attention
    /// because answering holds the delegator's turn for the length of the answer, and a delegator
    /// mid-answer cannot hand anything out.
    /// </summary>
    private static List<ChoiceRow> WorkModeChoices(string selected) => Choices(selected.ToLowerInvariant(),
        ("auto", "Agent decides", "Whatever the agent asks for when it announces."),
        ("delegateonly", "Delegate only", "Never answers itself, so it stays free to hand out the next thing."),
        ("delegateandwork", "Delegate and work", "Answers what nobody else can take, and is busy while it does."),
        ("workwhenalone", "Work when alone", "Answers only when there is no other agent to hand it to."));

    private static List<ChoiceRow> RoleChoices(string selected) => Choices(selected,
        ("member", "Member", "Joins rooms and talks. Cannot manage anyone."),
        ("admin", "Admin", "Manages users and agents, and sees every room an agent opens."));

    private static string Chosen(List<ChoiceRow> choices) =>
        choices.FirstOrDefault(c => c.RowClass.Contains("selected", StringComparison.Ordinal))?.Value ?? "";

    private static string RowClassFor(string name, string selected) =>
        string.Equals(name, selected, StringComparison.OrdinalIgnoreCase) ? "mgmt-row selected" : "mgmt-row";

    private static string[] Split(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
