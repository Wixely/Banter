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
    public List<TaskRow> Tasks { get; set; } = [];

    /// <summary>Rooms on the server the user has not joined. Hidden when there are none.</summary>
    public List<BrowseRow> Browse { get; set; } = [];
    public string BrowseClass { get; set; } = "browse hidden";
    public List<MessageRow> Messages { get; set; } = [];

    /// <summary>The tool-grants panel. Hidden until an operator opens it.</summary>
    public string ToolsClass { get; set; } = "toolpanel hidden";

    /// <summary>The entry point into the panel. Hidden for anyone the server refused a catalogue
    /// to, which is everyone except an admin — an inert button would only invite a refusal.</summary>
    public string ToolsButtonClass { get; set; } = "tools-open hidden";

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
}
