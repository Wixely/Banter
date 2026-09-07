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

    // ── Replies ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Id of the message this one answers, empty when it answers nothing.</summary>
    public string ReplyTo { get; set; } = "";

    /// <summary>"replying to nell: which of these..." — the quoted line, trimmed to one row.</summary>
    public string ReplyText { get; set; } = "";

    /// <summary>Hidden until this row actually answers something.</summary>
    public string ReplyClass { get; set; } = "reply-quote hidden";

    // ── An attached question (PLAN §8c-a) ───────────────────────────────────────────────────
    //
    // The controls hang off the message rather than opening over the window. Other agents are
    // working in this room and other people are reading it, so a modal would stop all of them to
    // ask one of them something — and a question nobody is looking at should still be here later.

    /// <summary>Server ask id, empty on every row that is not a question.</summary>
    public string AskId { get; set; } = "";

    /// <summary>Hidden until an ask arrives for this row, and again once it is answered.</summary>
    public string AskClass { get; set; } = "ask hidden";

    /// <summary>Two or three words naming the decision, from the question being shown.</summary>
    public string AskHeader { get; set; } = "";

    /// <summary>The question itself, as the asker worded it.</summary>
    public string AskText { get; set; } = "";

    /// <summary>
    /// One tab per question. An agent may ask two or three things that are really one decision in
    /// parts, and three separate questions in the timeline would read as three decisions.
    /// </summary>
    public List<AskTabRow> AskTabs { get; set; } = [];

    /// <summary>Hidden when there is only one question — a single tab is a label pretending to be
    /// a control.</summary>
    public string AskTabsClass { get; set; } = "ask-tabs hidden";

    /// <summary>The options of the question currently being shown, never of all of them.</summary>
    public List<AskOptionRow> AskOptions { get; set; } = [];

    /// <summary>"choose one", "choose any", or what is already chosen. Says what the controls do
    /// before somebody finds out by clicking.</summary>
    public string AskHint { get; set; } = "";

    /// <summary>Invitation to type instead, when the asker allows free text.</summary>
    public string AskWrite { get; set; } = "";

    /// <summary>Hidden when the asker asked for options only.</summary>
    public string AskWriteClass { get; set; } = "ask-write hidden";

    /// <summary>Label on the send control: says what will be sent, not just "OK".</summary>
    public string AskSendLabel { get; set; } = "Send";

    /// <summary>Greyed until something has actually been chosen or written.</summary>
    public string AskSendClass { get; set; } = "ask-send idle";
}

/// <summary>
/// One choice on an attached question. The mark rather than a real radio or checkbox: the row is
/// the hit target, and a glyph in a fixed-width slot lines up down the left the way a list should.
/// </summary>
[CupriBindable]
public sealed partial class AskOptionRow
{
    /// <summary>"askId|questionKey|value" — a click carries which question of which ask it
    /// answers, because a room may have several open at once.</summary>
    public string PickKey { get; set; } = "";

    public string Label { get; set; } = "";

    /// <summary>Why somebody would pick it. Blank for options that speak for themselves.</summary>
    public string Description { get; set; } = "";

    /// <summary>Hidden when the option carries no description, so the row does not gain a blank
    /// second line.</summary>
    public string DescriptionClass { get; set; } = "ask-desc hidden";

    /// <summary>The state glyph: a radio or a checkbox depending on the question.</summary>
    public string Mark { get; set; } = "";

    /// <summary>Drives styling: <c>ask-option</c> or <c>ask-option chosen</c>.</summary>
    public string RowClass { get; set; } = "ask-option";
}

/// <summary>One question of a multi-question ask, as a tab.</summary>
[CupriBindable]
public sealed partial class AskTabRow
{
    /// <summary>"askId|questionKey".</summary>
    public string TabKey { get; set; } = "";

    public string Label { get; set; } = "";

    /// <summary>Drives styling: <c>ask-tab</c>, <c>ask-tab active</c>, <c>ask-tab done</c> — so a
    /// reader can see at a glance which parts still need an answer.</summary>
    public string TabClass { get; set; } = "ask-tab";
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
    bool? WantsDelegator,
    AgentWorkMode? WorkMode);

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

/// <summary>One task on the work page, as the list shows it.</summary>
[CupriBindable]
public sealed partial class AdminTaskRow
{
    public string TaskId { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>"#main · claimed by scribe" — where it lives and who has it.</summary>
    public string Detail { get; set; } = "";

    /// <summary>Two letters for whoever holds it, or a dash when nobody does.</summary>
    public string Initials { get; set; } = "";

    public string State { get; set; } = "";
    public string StateClass { get; set; } = "mgmt-state";
    public string RowClass { get; set; } = "mgmt-row";
}

/// <summary>
/// One speaker and the voice they are heard in. A row per person or agent seen in a room, so the
/// list is who you actually talk to rather than a directory of everyone who ever existed.
/// </summary>
[CupriBindable]
public sealed partial class SpeakerVoiceRow
{
    public string Nick { get; set; } = "";
    public string Initials { get; set; } = "";

    /// <summary>The voice's label, or what is standing in for one when nothing is pinned.</summary>
    public string VoiceLabel { get; set; } = "";

    /// <summary>"pinned" when somebody chose it, "dealt" when the pool picked it by name.</summary>
    public string VoiceKind { get; set; } = "";

    public string RowClass { get; set; } = "mgmt-row";
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

    /// <summary>
    /// The message the composer is answering, or empty. One mechanism serves two things: replying
    /// to somebody in a busy room, and writing something an agent's buttons did not offer. Both
    /// are "this text belongs to that message", and giving each its own field would have meant two
    /// text boxes that behave almost but not quite the same.
    /// </summary>
    public string ReplyingId { get; set; } = "";

    /// <summary>"replying to nell" or "answering scribe" — said above the composer so it is not a
    /// mode you discover by pressing Enter.</summary>
    public string ReplyingText { get; set; } = "";

    /// <summary>Banner above the composer while a reply is being written; hidden otherwise.</summary>
    public string ReplyingClass { get; set; } = "replying-banner hidden";

    /// <summary>Whether "Reply" appears on the right-click menu. Hidden over system lines, which
    /// nobody is answering.</summary>
    public string ReplyItemClass { get; set; } = "menu-reply hidden";

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
    public string WorkButtonClass { get; set; } = "rail-button hidden";
    public string WorkPanelClass { get; set; } = "mgmt hidden";
    public string SettingsButtonClass { get; set; } = "rail-button";
    public string SettingsPanelClass { get; set; } = "mgmt hidden";

    /// <summary>Interface scale, as the settings page shows it ("100%").</summary>
    public string ZoomLabel { get; set; } = "100%";
    public List<ChoiceRow> ZoomChoices { get; set; } = [];

    // Voice. Transcription is what this machine hears with, speech is what it talks with, and the
    // voice list is who sounds like what — all three are about this machine, not the server.
    public List<ChoiceRow> TranscribeChoices { get; set; } = [];
    public string VoiceLanguage { get; set; } = "";
    public string VoiceVocabulary { get; set; } = "";
    public string VoiceEndpoint { get; set; } = "";
    public string VoiceWyomingTts { get; set; } = "";

    /// <summary>Hidden when nothing on this machine speaks: an empty voice picker is worse than
    /// none, because it looks like a list that failed to load.</summary>
    public string VoiceSpeakersClass { get; set; } = "mgmt-field hidden";
    public List<SpeakerVoiceRow> SpeakerVoices { get; set; } = [];

    // Destructive acts ask first. One dialog serves both pages: what differs is the sentence,
    // which is the only part that should differ.
    public string ConfirmClass { get; set; } = "confirm hidden";
    public string ConfirmTitle { get; set; } = "";
    public string ConfirmBody { get; set; } = "";
    public string ConfirmAction { get; set; } = "Remove";

    public List<AdminAgentRow> AdminAgents { get; set; } = [];
    public List<AdminUserRow> AdminUsers { get; set; } = [];

    public string AgentsStatus { get; set; } = "";
    public string UsersStatus { get; set; } = "";
    public string WorkStatus { get; set; } = "";

    // The work page: every room's tasks, for an operator rather than a participant. The roster's
    // Work strip stays what it is — this room, title and state, glanceable — because the two
    // answer different questions.
    public List<AdminTaskRow> AdminTasks { get; set; } = [];
    public string TaskSelected { get; set; } = "";
    public string TaskDetailClass { get; set; } = "mgmt-detail hidden";
    public string TaskEmptyClass { get; set; } = "mgmt-empty";
    public string TaskDetailTitle { get; set; } = "";
    public string TaskDetailSubtitle { get; set; } = "";
    public string TaskRoom { get; set; } = "";
    public string TaskState { get; set; } = "";
    public string TaskPoster { get; set; } = "";
    public string TaskAssignee { get; set; } = "";
    public string TaskBody { get; set; } = "";
    public string TaskResult { get; set; } = "";
    public string TaskResultClass { get; set; } = "mgmt-field hidden";
    public string TaskTimes { get; set; } = "";
    public string TaskLease { get; set; } = "";
    public string TaskId { get; set; } = "";
    public List<ChoiceRow> TaskScopeChoices { get; set; } = [];

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
    public List<ChoiceRow> AgentWorkModeChoices { get; set; } = [];

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

    /// <summary>
    /// The chat pane, taken out of the document while the sign-in screen is up.
    ///
    /// <para>Not cosmetic, and not only tidiness. It holds the composer and Send — the only
    /// focusables outside the sign-in form — and Tab reaching the composer of a session nobody
    /// is signed in to is wrong on its own. It also holds the timeline's
    /// <c>cupri-context-menu</c>, and one of those containing menu items disables Tab focus
    /// traversal for the <i>entire</i> document in CupriFace 0.18.0 and 0.19.0 (measured), which
    /// is why Tab moved nothing anywhere in this application before this existed.</para>
    /// </summary>
    public string MainClass { get; set; } = "main";

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

    /// <summary>
    /// How this head will hold the password once the sign-in works — supplied by the head,
    /// because the answer is DPAPI on Windows and file permissions elsewhere. On screen rather
    /// than in a log, so staying signed in is a decision made knowing what it means.
    /// </summary>
    public string ConnectHint { get; set; } = "";

    /// <summary>
    /// Who this client is signed in as, shown on the settings page above the sign-out control.
    /// Kept apart from <see cref="Nick"/>, which is what the server calls this session: these two
    /// answer "which account on which server" rather than "who am I in this room".
    /// </summary>
    public string AccountServer { get; set; } = "";

    public string AccountUser { get; set; } = "";

    /// <summary>
    /// The sign-out control. Hidden on a head with nothing to sign out of — one given its
    /// account on the command line every time has no session to end, only a process to close.
    /// </summary>
    public string SignOutClass { get; set; } = "mgmt-remove sign-out hidden";
}
