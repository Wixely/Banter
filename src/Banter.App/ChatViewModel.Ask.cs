using Banter.Protocol;

namespace Banter.App;

/// <summary>
/// Questions an agent has attached to a message, and what the person reading has chosen so far
/// (PLAN §8c-a).
///
/// <para>The controls live on the message, not in a dialogue over the window. Several agents may
/// be working in a room at once and more than one of them may be asking something; a modal would
/// stop everybody to serve whichever question happened to arrive first, and would take the
/// question off the screen the moment it was dismissed.</para>
///
/// <para>What somebody types goes through the composer rather than a field inside the row.
/// <c>data-repeat</c> substitutes bindings and discards the row object, so a text field in there
/// has nothing to write back to — but more to the point, the composer is already the one place in
/// this app where text is typed, and answering a question is a kind of reply.</para>
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>An open question and the answer being assembled for it.</summary>
    private sealed class OpenAsk(string room, AskPayload ask)
    {
        public string Room { get; } = room;
        public AskPayload Ask { get; } = ask;

        /// <summary>Which question is showing. An index rather than a key, so an ask whose author
        /// gave two questions the same key can still be moved between.</summary>
        public int Active { get; set; }

        /// <summary>Chosen values per question key. A list even for a single-choice question, so
        /// the two kinds differ only in whether a pick replaces or adds.</summary>
        public Dictionary<string, List<string>> Chosen { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, OpenAsk> _openAsks = new(StringComparer.Ordinal);

    /// <summary>
    /// Attaches a question to the message that announced it. If that message has not arrived yet
    /// the ask is still kept, and <see cref="RestoreAsks"/> picks it up once the row exists.
    /// </summary>
    public void AddAsk(AskPayload ask)
    {
        if (ask.AskId.Length == 0)
        {
            return;
        }

        _openAsks[ask.AskId] = new OpenAsk(ask.Room, ask);
        RenderAsk(ask.AskId);
    }

    /// <summary>
    /// Takes a question off the screen once it has been answered — by anybody, in any client. A
    /// second answer is not a conflict to resolve, it is one that should not arise.
    /// </summary>
    public void CloseAsk(string askId, string answeredBy)
    {
        if (!_openAsks.TryGetValue(askId, out var open))
        {
            return;
        }

        _openAsks.Remove(askId);

        // The composer must not stay pointed at a question that is over.
        if (Model.ReplyingId.Length > 0 && Model.ReplyingId == open.Ask.MessageId)
        {
            ClearReply();
        }

        var row = Find(open.Room, open.Ask.MessageId ?? "");
        if (row is null)
        {
            return;
        }

        row.AskClass = "ask answered";
        row.AskTabsClass = "ask-tabs hidden";
        row.AskOptions = [];
        row.AskWriteClass = "ask-write hidden";
        row.AskSendClass = "ask-send hidden";
        row.AskHeader = "";
        row.AskText = "";
        row.AskHint = answeredBy.Length > 0 ? $"answered by {answeredBy}" : "no longer waiting for an answer";
    }

    /// <summary>Shows a different question of the same ask.</summary>
    public void SelectAskTab(string tabKey)
    {
        var (askId, questionKey, _) = SplitKey(tabKey);
        if (!_openAsks.TryGetValue(askId, out var open))
        {
            return;
        }

        var index = IndexOf(open, questionKey);
        if (index >= 0)
        {
            open.Active = index;
            RenderAsk(askId);
        }
    }

    /// <summary>
    /// Chooses, or un-chooses, one option. Single-choice questions replace; multi-select ones
    /// toggle — and clicking the chosen option of a single-choice question clears it, because
    /// there is otherwise no way back out of a decision made by a misplaced click.
    /// </summary>
    public void PickAskOption(string pickKey)
    {
        var (askId, questionKey, value) = SplitKey(pickKey);
        if (!_openAsks.TryGetValue(askId, out var open))
        {
            return;
        }

        var question = open.Ask.Questions.FirstOrDefault(q => q.Key == questionKey);
        if (question is null)
        {
            return;
        }

        var chosen = open.Chosen.TryGetValue(questionKey, out var already)
            ? already
            : open.Chosen[questionKey] = [];

        if (chosen.Contains(value))
        {
            chosen.Remove(value);
        }
        else
        {
            if (!question.MultiSelect)
            {
                chosen.Clear();
            }

            chosen.Add(value);
        }

        RenderAsk(askId);
    }

    /// <summary>
    /// The answer as the protocol wants it, or null when there is nothing to send. Text the person
    /// wrote rides along with what they clicked rather than replacing it: somebody who typed
    /// something typed it because the buttons did not say what they meant.
    /// </summary>
    public AnswerPayload? BuildAnswer(string askId, string written)
    {
        if (!_openAsks.TryGetValue(askId, out var open))
        {
            return null;
        }

        var text = written.Trim();
        var showing = Active(open)?.Key ?? "";

        var answers = open.Ask.Questions
            .Select(q => new AskAnswer(
                q.Key,
                open.Chosen.TryGetValue(q.Key, out var chosen) ? [.. chosen] : [],
                // Free text answers the question that is showing. Attaching it to all of them
                // would put the same sentence against three different decisions.
                q.Key == showing ? text : ""))
            .Where(a => a.Chosen.Count > 0 || a.Text.Length > 0)
            .ToList();

        return answers.Count == 0 ? null : new AnswerPayload(askId, answers);
    }

    /// <summary>Whether an ask is still open here, so a caller can tell a stale click from a live
    /// one without reaching into the dictionary.</summary>
    public bool IsAskOpen(string askId) => _openAsks.ContainsKey(askId);

    /// <summary>The room an open ask belongs to, or empty.</summary>
    public string AskRoom(string askId) => _openAsks.TryGetValue(askId, out var open) ? open.Room : "";

    /// <summary>The message an open ask hangs off, or empty.</summary>
    public string AskMessage(string askId) =>
        _openAsks.TryGetValue(askId, out var open) ? open.Ask.MessageId ?? "" : "";

    /// <summary>
    /// Re-attaches every open ask to its row. Called after a room is switched into or older
    /// history is paged in, when the row an ask needs may only just have been built.
    /// </summary>
    public void RestoreAsks()
    {
        foreach (var askId in _openAsks.Keys.ToList())
        {
            RenderAsk(askId);
        }
    }

    private static AskQuestion? Active(OpenAsk open) =>
        open.Active >= 0 && open.Active < open.Ask.Questions.Count ? open.Ask.Questions[open.Active] : null;

    private static int IndexOf(OpenAsk open, string questionKey)
    {
        for (var i = 0; i < open.Ask.Questions.Count; i++)
        {
            if (open.Ask.Questions[i].Key == questionKey)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>"askId|questionKey|value", the whole of what a click needs to know. A room may
    /// have several questions open at once, so a bare value would not say what it answers.</summary>
    private static (string AskId, string QuestionKey, string Value) SplitKey(string key)
    {
        var parts = key.Split('|');
        return (
            parts.Length > 0 ? parts[0] : "",
            parts.Length > 1 ? parts[1] : "",
            parts.Length > 2 ? string.Join("|", parts[2..]) : "");
    }

    private void RenderAsk(string askId)
    {
        if (!_openAsks.TryGetValue(askId, out var open))
        {
            return;
        }

        var row = Find(open.Room, open.Ask.MessageId ?? "");
        var question = Active(open);
        if (row is null || question is null)
        {
            return;
        }

        var chosen = open.Chosen.TryGetValue(question.Key, out var picked) ? picked : [];

        row.AskId = askId;
        row.AskClass = "ask";
        row.AskHeader = question.Header.Length > 0 ? question.Header : question.Key;
        row.AskText = question.Text;

        row.AskTabs =
        [
            .. open.Ask.Questions.Select((q, i) => new AskTabRow
            {
                TabKey = $"{askId}|{q.Key}",
                Label = q.Header.Length > 0 ? q.Header : q.Key,
                TabClass = i == open.Active
                    ? "ask-tab active"
                    : open.Chosen.TryGetValue(q.Key, out var answered) && answered.Count > 0
                        ? "ask-tab done"
                        : "ask-tab",
            }),
        ];

        // One tab is a label pretending to be a control.
        row.AskTabsClass = open.Ask.Questions.Count > 1 ? "ask-tabs" : "ask-tabs hidden";

        row.AskOptions =
        [
            .. question.Options.Select(o => new AskOptionRow
            {
                PickKey = $"{askId}|{question.Key}|{o.Value}",
                Label = o.Label,
                Description = o.Description,
                DescriptionClass = o.Description.Length > 0 ? "ask-desc" : "ask-desc hidden",
                // A box beside a multi-select and a dot beside a single choice, so the shape of
                // the decision is visible before anything is clicked rather than discovered by
                // clicking a second option and watching the first one vanish.
                Mark = question.MultiSelect
                    ? chosen.Contains(o.Value) ? "☑" : "☐"
                    : chosen.Contains(o.Value) ? "◉" : "○",
                RowClass = chosen.Contains(o.Value) ? "ask-option chosen" : "ask-option",
            }),
        ];

        row.AskHint = question.Options.Count == 0
            ? "write an answer"
            : question.MultiSelect
                ? "choose any"
                : "choose one";

        var answering = Model.ReplyingId.Length > 0 && Model.ReplyingId == open.Ask.MessageId;
        row.AskWrite = answering ? "typing an answer below — Esc to cancel" : "or write your own answer";
        row.AskWriteClass = question.AllowText ? "ask-write" : "ask-write hidden";

        row.AskSendLabel = chosen.Count > 1 ? $"Send {chosen.Count}" : "Send";
        row.AskSendClass = chosen.Count > 0 || answering ? "ask-send" : "ask-send idle";
    }
}
