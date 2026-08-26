using CupriFace.Binding;

namespace Banter.App;

/// <summary>
/// One physical line of a message. Messages render a line at a time because the engine collapses
/// newlines in bound text and ignores <c>white-space</c> — see <see cref="MessageRow.Text"/>.
/// </summary>
[CupriBindable]
public sealed partial class TextLine
{
    public string Value { get; set; } = "";
}

/// <summary>
/// One rendered row in the timeline. Rows wrap to their own height — CupriFace 0.4.0 measures
/// realised rows and replaces the <c>item-height</c> estimate, so nothing here needs a fixed size.
/// </summary>
[CupriBindable]
public sealed partial class MessageRow
{
    /// <summary>Server message id, when there is one. Used to keep a page of older history from
    /// duplicating a message the live feed already delivered. Empty for local system lines.</summary>
    public string Id { get; set; } = "";

    public string Sender { get; set; } = "";

    /// <summary>
    /// The message as sent. Assigning re-splits <see cref="Lines"/>, which is what actually gets
    /// rendered: CupriFace collapses newlines in bound text and ignores <c>white-space</c>, so a
    /// multi-line message drawn as one bound value comes out as a single run-on line.
    /// </summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            SetLines(value);
        }
    }

    private string _text = "";

    /// <summary>The message split for rendering. One entry even for a single-line message.</summary>
    public List<TextLine> Lines { get; set; } = [];

    /// <summary>Non-breaking space: a blank line still has to take up height, or paragraph
    /// breaks vanish. An ordinary space collapses away; this does not.</summary>
    private const string BlankLine = "\u00a0";

    private void SetLines(string value)
    {
        Lines.Clear();
        foreach (var line in value.ReplaceLineEndings("\n").Split('\n'))
        {
            Lines.Add(new TextLine { Value = line.Length == 0 ? BlankLine : line });
        }
    }

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

    /// <summary>Hidden until the room actually has work on the board.</summary>
    public string TasksClass { get; set; } = "tasks hidden";

    public List<RoomRow> Rooms { get; set; } = [];
    public List<AgentRow> Agents { get; set; } = [];
    public List<TaskRow> Tasks { get; set; } = [];

    /// <summary>Rooms on the server the user has not joined. Hidden when there are none.</summary>
    public List<BrowseRow> Browse { get; set; } = [];
    public string BrowseClass { get; set; } = "browse hidden";
    public List<MessageRow> Messages { get; set; } = [];
}
