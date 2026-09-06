namespace Banter.App;

/// <summary>
/// Replies: saying which message you are answering.
///
/// <para>In a room with one conversation this is a nicety. In a room with four agents working at
/// once it is the difference between an answer and a guess — "the second one" is unreadable to
/// anybody, human or model, who has to decide for themselves which of the last ten lines it came
/// back to. The same mechanism carries free-text answers to an agent's question, because those
/// are replies too, and a second text box that behaved almost-but-not-quite like the composer
/// would be worse than one that behaves exactly like it.</para>
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>How much of the quoted message a reply shows above itself. Enough to recognise
    /// which line it was, not enough to repeat it.</summary>
    private const int QuoteLength = 80;

    /// <summary>
    /// Points the composer at a message. <paramref name="answering"/> only changes the wording —
    /// answering a question and replying to a remark are the same act, and the banner says which
    /// one it is so the mode is not something discovered by pressing Enter.
    /// </summary>
    public void BeginReply(string messageId, bool answering = false)
    {
        var row = Find(Model.ActiveRoom, messageId);
        if (row is null)
        {
            return;
        }

        Model.ReplyingId = messageId;
        Model.ReplyingText = answering
            ? $"answering {row.Sender} — Esc to cancel"
            : $"replying to {row.Sender} — Esc to cancel";
        Model.ReplyingClass = "replying-banner";

        // An open ask shows "typing an answer" once the composer is pointed at it.
        RestoreAsks();
    }

    /// <summary>Stops replying, leaving whatever is typed alone. Cancelling the target is not the
    /// same as throwing away the words.</summary>
    public void ClearReply()
    {
        if (Model.ReplyingId.Length == 0)
        {
            return;
        }

        Model.ReplyingId = "";
        Model.ReplyingText = "";
        Model.ReplyingClass = "replying-banner hidden";
        RestoreAsks();
    }

    /// <summary>The message the composer is pointed at, empty when it is not pointed anywhere.</summary>
    public string ReplyingTo => Model.ReplyingId;

    /// <summary>
    /// Whether "Reply" belongs on the right-click menu over this row. Not over system lines: they
    /// have no author and answering the server is not a conversation anybody is having.
    /// </summary>
    public void SetReplyTarget(string room, string messageId)
    {
        var row = Find(room, messageId);
        Model.ReplyItemClass = row is null || row.RowClass.Contains("system", StringComparison.Ordinal)
            ? "menu-reply hidden"
            : "menu-reply";
    }

    /// <summary>
    /// Shows what a row is answering, as a quoted line above it. Called as rows arrive and again
    /// when older history is paged in, because the message being quoted is often older than the
    /// reply and may not have been on screen when the reply landed.
    /// </summary>
    public void ResolveReply(string room, MessageRow row)
    {
        if (row.ReplyTo.Length == 0)
        {
            return;
        }

        var quoted = Find(room, row.ReplyTo);
        if (quoted is null)
        {
            // Named but not here yet. Say so rather than silently dropping the thread: a reply
            // whose parent is off the top of the scrollback is still visibly a reply.
            row.ReplyText = "replying to an earlier message";
            row.ReplyClass = "reply-quote";
            return;
        }

        var text = quoted.Text.ReplaceLineEndings(" ").Trim();
        if (text.Length > QuoteLength)
        {
            text = text[..QuoteLength] + "…";
        }

        row.ReplyText = $"{quoted.Sender}: {text}";
        row.ReplyClass = "reply-quote";
    }

    /// <summary>Re-runs <see cref="ResolveReply"/> over a room, for after a page of older history
    /// has arrived and quotes that could not be resolved before now can be.</summary>
    public void ResolveReplies(string room)
    {
        if (!_rooms.TryGetValue(room, out var rows))
        {
            return;
        }

        foreach (var row in rows)
        {
            ResolveReply(room, row);
        }
    }
}
