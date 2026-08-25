using CupriFace.Binding;

namespace Banter.App;

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
}

/// <summary>A joined room in the sidebar.</summary>
[CupriBindable]
public sealed partial class RoomRow
{
    public string Name { get; set; } = "";
    public string TabClass { get; set; } = "tab";
    public string Badge { get; set; } = "";
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

    public List<RoomRow> Rooms { get; set; } = [];
    public List<MessageRow> Messages { get; set; } = [];
}
