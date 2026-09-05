using Banter.Protocol;
using CupriFace.Binding;

namespace Banter.App;

/// <summary>
/// One rendered row in the timeline. Rows wrap to their own height — CupriFace measures realised
/// rows and replaces the <c>item-height</c> estimate, so nothing here needs a fixed size.
/// </summary>
[CupriBindable]
public sealed partial class MessageRow
{
    /// <summary>Server message id, when there is one. Used to keep a page of older history from
    /// duplicating a message the live feed already delivered. Empty for local system lines.</summary>
    public string Id { get; set; } = "";

    public string Sender { get; set; } = "";

    /// <summary>
    /// The message as sent, newlines included. Rendered directly: the timeline styles it
    /// <c>white-space: pre-wrap</c>, which CupriFace 0.5.0 honours, so hard breaks survive and
    /// long lines still wrap. Measured: <c>\n</c>, <c>\r\n</c> and a bare <c>\r</c> all break.
    /// </summary>
    public string Text { get; set; } = "";

    public string Time { get; set; } = "";

    /// <summary>
    /// Drives styling: <c>line</c>, <c>line own</c>, <c>line system</c>, <c>line streaming</c>.
    /// A class string rather than booleans because the cascade does the work in CSS.
    /// </summary>
    public string RowClass { get; set; } = "line";

    /// <summary>
    /// Two letters standing in for an avatar. Initials rather than a colour block because a room
    /// is read by scanning down the left edge, and a letter is recognisable where a hue is not.
    /// </summary>
    public string Initials { get; set; } = "";

    /// <summary>
    /// " (edited)" once the author has changed it, empty otherwise. Shown because the words a
    /// reader is looking at may not be the ones somebody else replied to.
    /// </summary>
    public string EditedMark { get; set; } = "";

    /// <summary>Attached file, when the message carries one. Empty otherwise.</summary>
    public string FileId { get; set; } = "";

    /// <summary>Hidden until the row actually has an attachment.</summary>
    public string AttachClass { get; set; } = "attach hidden";

    /// <summary>Name and size, filled in once the server's file metadata arrives.</summary>
    public string AttachText { get; set; } = "";

    /// <summary>
    /// <c>file://</c> URI of a downloaded image attachment, shown inline. Empty for everything
    /// else — a PDF or a zip stays a chip, because a preview of it would be a grey box.
    /// </summary>
    public string ImageSrc { get; set; } = "";

    /// <summary>Hidden until an image has actually been fetched and written to the cache.</summary>
    public string ImageClass { get; set; } = "inline-image hidden";
}

/// <summary>
/// An agent present in the active room, with the attributes the delegator routes on (PLAN §8a).
/// Shown so a human can see who is in the room and, crucially, which of them are third-party.
/// </summary>
[CupriBindable]
public sealed partial class AgentRow
{
    public string Nick { get; set; } = "";

    /// <summary>Two letters standing in for an avatar, as in the timeline.</summary>
    public string Initials { get; set; } = "";

    /// <summary>"local" or "frontier" — the axis that decides whether data leaves.</summary>
    public string Locality { get; set; } = "";

    public string Skills { get; set; } = "";

    /// <summary>Marker shown beside the delegator, empty for everyone else.</summary>
    public string Role { get; set; } = "";

    /// <summary>Drives styling: <c>agent</c>, <c>agent frontier</c>, <c>agent delegator</c>.</summary>
    public string RowClass { get; set; } = "agent";
}

/// <summary>A unit of work on the room's board (PLAN §8b).</summary>
[CupriBindable]
public sealed partial class TaskRow
{
    public string TaskId { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>"open", "claimed by dagger", "done", "failed" — state and holder in one line.</summary>
    public string Status { get; set; } = "";

    /// <summary>Drives styling: <c>task</c>, <c>task held</c>, <c>task done</c>, <c>task failed</c>.</summary>
    public string RowClass { get; set; } = "task";
}

/// <summary>A joined room in the sidebar.</summary>
[CupriBindable]
public sealed partial class RoomRow
{
    public string Name { get; set; } = "";

    /// <summary>Indented and prefixed for a sub-room, so parentage is visible in the list.</summary>
    public string Label { get; set; } = "";

    public string TabClass { get; set; } = "tab";
    public string Badge { get; set; } = "";

    /// <summary>
    /// Carries the badge's visibility. An empty badge still paints its background and padding, so
    /// without this every room with nothing unread wore a small blank pill.
    /// </summary>
    public string BadgeClass { get; set; } = "badge hidden";
}

/// <summary>
/// One tool the server has connected, in the grants panel. Tools run on the server, so this row
/// is an operator control — nothing here gives the client any access of its own (PLAN §8).
/// </summary>
[CupriBindable]
public sealed partial class ToolRow
{
    public string Name { get; set; } = "";

    /// <summary>Which upstream serves it, so an operator can see what they are opening up.</summary>
    public string Server { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Tick when the selected agent holds this tool, blank when it does not.</summary>
    public string Mark { get; set; } = "";

    /// <summary>Drives styling: <c>tool</c> or <c>tool granted</c>.</summary>
    public string RowClass { get; set; } = "tool";
}

/// <summary>An agent whose grants can be edited, in the panel's left column.</summary>
[CupriBindable]
public sealed partial class ToolAgentRow
{
    public string Nick { get; set; } = "";

    /// <summary>"3 of 12 tools" — enough to see at a glance who is holding a lot.</summary>
    public string Summary { get; set; } = "";

    public string RowClass { get; set; } = "tool-agent";
}

/// <summary>An agent form as something to send — the create and the save paths take the same
/// shape, because a create IS a save of something that did not exist yet.</summary>
public sealed record AgentForm(
    string Nick,
    string[] Rooms,
    string[] Skills,
    AgentLocality Locality,
    DataSensitivity Clearance,
    int? CostTier,
    bool? WantsDelegator);

/// <summary>One agent identity on the agents page.</summary>
[CupriBindable]
public sealed partial class AdminAgentRow
{
    public string Nick { get; set; } = "";
    public string Initials { get; set; } = "";

    /// <summary>"local · sensitive · #main" — the routing attributes, read at a glance.</summary>
    public string Detail { get; set; } = "";

    /// <summary>The key fingerprint, or what is missing instead.</summary>
    public string State { get; set; } = "";

    public string StateClass { get; set; } = "admin-state";

    public string RowClass { get; set; } = "mgmt-row";
}

/// <summary>One user account on the users page.</summary>
[CupriBindable]
public sealed partial class AdminUserRow
{
    public string Username { get; set; } = "";
    public string Initials { get; set; } = "";

    /// <summary>"admin" or "member" — the one attribute a user has.</summary>
    public string Detail { get; set; } = "";

    public bool IsAdmin { get; set; }

    public string RowClass { get; set; } = "mgmt-row";
}

/// <summary>One human in the room's roster. The section heading is what says they are not an
/// agent; the row itself only needs who they are and whether they hold a mode worth seeing.</summary>
[CupriBindable]
public sealed partial class RosterUserRow
{
    public string Nick { get; set; } = "";
    public string Initials { get; set; } = "";

    /// <summary>"op" for operators, empty for everyone else — worn like the delegator's marker.</summary>
    public string Badge { get; set; } = "";

    public string RowClass { get; set; } = "member";
}

/// <summary>
/// One option in a radio-card group — the shape both management pages use for every choice, so
/// locality, clearance, the delegator override and a user's role are all the same control.
/// </summary>
[CupriBindable]
public sealed partial class ChoiceRow
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>What choosing this actually means. The reason these are cards, not a dropdown:
    /// "frontier" is a word, "anything it is shown leaves this machine" is a decision.</summary>
    public string Hint { get; set; } = "";

    public string RowClass { get; set; } = "mgmt-choice";
    public string DotClass { get; set; } = "mgmt-dot";
}

/// <summary>One agent offered while an "@" is being typed.</summary>
[CupriBindable]
public sealed partial class MentionRow
{
    public string Nick { get; set; } = "";

    /// <summary>The same two letters the timeline and roster use, so the eye recognises the row.</summary>
    public string Initials { get; set; } = "";

    /// <summary>"local · chat, code" — locality first, because it decides whether data leaves.</summary>
    public string Meta { get; set; } = "";

    /// <summary>Drives styling: <c>mention</c> or <c>mention selected</c>.</summary>
    public string RowClass { get; set; } = "mention";
}

/// <summary>A room on the server the user is not in, offered for joining.</summary>
[CupriBindable]
public sealed partial class BrowseRow
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Members { get; set; } = "";
}

/// <summary>
/// Everything the view binds to. Plain properties and lists — CupriFace re-reads the object on
/// <c>Refresh()</c>, so there is no change notification to implement.
/// </summary>
[CupriBindable]
public sealed partial class ChatModel
{
    public string ActiveRoom { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Status { get; set; } = "Disconnected";
    public string StatusClass { get; set; } = "status off";
    public string Composer { get; set; } = "";

    /// <summary>This account's initials, for the rail and the sidebar footer.</summary>
    public string NickInitials { get; set; } = "";

    /// <summary>
    /// The message the composer is currently rewriting, or empty when it is composing a new one.
    /// Editing reuses the composer rather than opening a field over the timeline: it is the only
    /// place in this app where text is typed, and a second one would need its own keyboard, IME
    /// and paste handling for no gain.
    /// </summary>
    public string EditingId { get; set; } = "";

    /// <summary>Banner above the composer while an edit is in progress; hidden otherwise.</summary>
    public string EditingClass { get; set; } = "editing-banner hidden";

    /// <summary>Whether "Edit" appears on the right-click menu — only over your own messages,
    /// because the server refuses anyone else and offering it would be a lie.</summary>
    public string EditItemClass { get; set; } = "menu-edit hidden";

    /// <summary>Whether "Delete" appears. Shown over any message: the author may remove their
    /// own and an admin may remove anyone's, and which of those applies is the server's to say.</summary>
    public string DeleteItemClass { get; set; } = "menu-delete hidden";
    public string Nick { get; set; } = "";

    /// <summary>Label for the load-earlier control; also carries its own visibility class.</summary>
    public string LoadOlderClass { get; set; } = "loadmore hidden";
    public string LoadOlderText { get; set; } = "Load earlier messages";

    /// <summary>Who is dispatching in the active room, or a note that nobody is.</summary>
    public string Delegator { get; set; } = "";
    public string DispatchMode { get; set; } = "";

    /// <summary>
    /// The two above, joined for display. Composed here rather than in the markup because a
    /// separator written between two bindings still renders when both are empty, and a room with
    /// no agents in it showed a stray "·" floating in its header.
    /// </summary>
    public string Dispatch { get; set; } = "";

    /// <summary>Hidden until the room actually has work on the board.</summary>
    public string TasksClass { get; set; } = "tasks hidden";

    public List<RoomRow> Rooms { get; set; } = [];
    public List<AgentRow> Agents { get; set; } = [];
    public List<RosterUserRow> Users { get; set; } = [];

    // Section labels in the roster, hidden when their section is empty — a heading over nothing
    // reads as something failing to load.
    public string RosterAgentsTitleClass { get; set; } = "roster-title hidden";
    public string RosterUsersTitleClass { get; set; } = "roster-title hidden";
    public List<TaskRow> Tasks { get; set; } = [];

    /// <summary>Rooms on the server the user has not joined. Hidden when there are none.</summary>
    public List<BrowseRow> Browse { get; set; } = [];

    /// <summary>Agents matching the "@" being typed, empty when the list is down.</summary>
    public List<MentionRow> Mentions { get; set; } = [];

    /// <summary>Drives the suggestion popup: <c>mentions</c> or <c>mentions hidden</c>.</summary>
    public string MentionsClass { get; set; } = "mentions hidden";

    // ---- The agents page (admin only) ----

    /// <summary>Drives the rail button: <c>admin-open</c>, or hidden for anyone who is not an admin.</summary>
    // ── Management pages ────────────────────────────────────────────────────────────────────
    // Agents and users are the same master-detail page: a list with a "new" button on the left,
    // a form for whatever is selected on the right, one footer. They share every CSS class and
    // one markup template (BanterChatApp.ManagementPage), so these are the two sets of bindings
    // that template is instantiated with — the only place the two pages are allowed to differ.
    public string AgentsButtonClass { get; set; } = "rail-button hidden";
    public string UsersButtonClass { get; set; } = "rail-button hidden";
    public string AgentsPanelClass { get; set; } = "mgmt hidden";
    public string UsersPanelClass { get; set; } = "mgmt hidden";

    public List<AdminAgentRow> AdminAgents { get; set; } = [];
    public List<AdminUserRow> AdminUsers { get; set; } = [];

    public string AgentsStatus { get; set; } = "";
    public string UsersStatus { get; set; } = "";

    /// <summary>The one-shot secret banner — an enrolment code or a temporary password. Shared
    /// because only one page is ever open, and because both secrets behave identically.</summary>
    public string AdminCode { get; set; } = "";
    public string AdminCodeClass { get; set; } = "mgmt-secret hidden";
    public string AdminCodeFor { get; set; } = "";

    // Agents detail pane.
    public string AgentSelected { get; set; } = "";
    public string AgentDetailClass { get; set; } = "mgmt-detail hidden";
    public string AgentEmptyClass { get; set; } = "mgmt-empty";
    public string AgentDetailTitle { get; set; } = "";
    public string AgentDetailSubtitle { get; set; } = "";
    public string AgentRemoveClass { get; set; } = "mgmt-remove hidden";
    public string AgentDirtyClass { get; set; } = "mgmt-dirty hidden";
    public string AgentSaveLabel { get; set; } = "Save changes";
    public string AgentNickFieldClass { get; set; } = "mgmt-field";
    public string AgentKeyFieldClass { get; set; } = "mgmt-field hidden";
    public string AgentReissueClass { get; set; } = "mgmt-inline hidden";
    public string AgentFormNick { get; set; } = "";
    public string AgentFormNickReadonly { get; set; } = "";
    public string AgentFormRooms { get; set; } = "";
    public string AgentFormSkills { get; set; } = "";
    public string AgentFormCost { get; set; } = "";
    public string AgentFingerprint { get; set; } = "";
    public List<ChoiceRow> AgentLocalityChoices { get; set; } = [];
    public List<ChoiceRow> AgentClearanceChoices { get; set; } = [];
    public List<ChoiceRow> AgentDelegatorChoices { get; set; } = [];

    // Users detail pane.
    public string UserSelected { get; set; } = "";
    public string UserDetailClass { get; set; } = "mgmt-detail hidden";
    public string UserEmptyClass { get; set; } = "mgmt-empty";
    public string UserDetailTitle { get; set; } = "";
    public string UserDetailSubtitle { get; set; } = "";
    public string UserRemoveClass { get; set; } = "mgmt-remove hidden";
    public string UserDirtyClass { get; set; } = "mgmt-dirty hidden";
    public string UserSaveLabel { get; set; } = "Save changes";
    public string UserNickFieldClass { get; set; } = "mgmt-field";
    public string UserResetClass { get; set; } = "mgmt-inline hidden";
    public string UserFormName { get; set; } = "";
    public string UserFormNameReadonly { get; set; } = "";
    public List<ChoiceRow> UserRoleChoices { get; set; } = [];
    public string BrowseClass { get; set; } = "browse hidden";
    public List<MessageRow> Messages { get; set; } = [];

    /// <summary>The tool-grants panel. Hidden until an operator opens it.</summary>
    public string ToolsClass { get; set; } = "toolpanel hidden";

    /// <summary>The entry point into the panel. Hidden for anyone the server refused a catalogue
    /// to, which is everyone except an admin — an inert button would only invite a refusal.</summary>
    public string ToolsButtonClass { get; set; } = "rail-button hidden";

    /// <summary>Whose grants are being edited.</summary>
    public string ToolsAgent { get; set; } = "";

    /// <summary>The panel's heading, with the agent's name only once one is chosen.</summary>
    public string ToolsTitle { get; set; } = "Tools";

    /// <summary>What just happened: saved, refused, or what the panel is waiting on.</summary>
    public string ToolsStatus { get; set; } = "";

    public List<ToolAgentRow> ToolAgents { get; set; } = [];
    public List<ToolRow> ToolCatalog { get; set; } = [];

    /// <summary>
    /// The microphone control. Hidden on a head that wired no capture backend, because a button
    /// that cannot do anything is worse than no button.
    /// </summary>
    public string MicClass { get; set; } = "mic hidden";

    public string MicText { get; set; } = "Talk";

    /// <summary>What the microphone is doing, in words: idle, listening, heard, transcribing.</summary>
    public string VoiceStatus { get; set; } = "";

    /// <summary>The readback toggle's label, which doubles as its state.</summary>
    public string ReadbackText { get; set; } = "Speech: agents";

    public string ReadbackClass { get; set; } = "readback hidden";

    /// <summary>
    /// The strip under the composer. Hidden outright on a head with no audio, so it does not sit
    /// there as an empty band of padding.
    /// </summary>
    public string VoiceRowClass { get; set; } = "voice-row hidden";

    /// <summary>The attach control. Hidden on a head that cannot open a file dialog.</summary>
    public string AttachButtonClass { get; set; } = "attach-open hidden";

    /// <summary>
    /// The connect screen. Heads that are given a server on the command line never show it; a
    /// phone has no command line, so it is how an account is entered there.
    /// </summary>
    public string ConnectClass { get; set; } = "connect hidden";

    public string ConnectServer { get; set; } = "";
    public string ConnectUser { get; set; } = "";

    /// <summary>
    /// Bound to a password field, and deliberately cleared the moment it has been used. It is not
    /// written to the settings file for the same reason nothing else secret is.
    /// </summary>
    public string ConnectPassword { get; set; } = "";

    /// <summary>What the connect screen is doing, or why the last attempt failed.</summary>
    public string ConnectStatus { get; set; } = "";

    public string ConnectButtonText { get; set; } = "Connect";
}
