using Banter.App;
using Banter.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Saying which message you are answering.
///
/// <para>In a room with one conversation this is a nicety. In a room with four agents working at
/// once it is the difference between an answer and a guess: "the second one" is unreadable to
/// anybody, human or model, who has to decide for themselves which of the last ten lines it came
/// back to.</para>
/// </summary>
public sealed class ReplyTests(ITestOutputHelper output)
{
    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("nell");
        vm.AddRoom("#main");
        vm.Append("#main", "root", "which of these should we do first?", 0, id: "asked");
        return vm;
    }

    [Fact]
    public void AReplyQuotesWhatItIsAnswering()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "the second one", 0, id: "reply", replyTo: "asked");

        var row = vm.FindMessage("#main", "reply")!;
        output.WriteLine(row.ReplyText);

        // Quoted, not merely threaded: the message being answered is usually well off the top of
        // the screen, and a thread you have to scroll to follow is one nobody follows.
        Assert.Contains("root", row.ReplyText, StringComparison.Ordinal);
        Assert.Contains("which of these", row.ReplyText, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", row.ReplyClass, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryMessageQuotesNothing()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "unrelated thought", 0, id: "plain");

        // A quote on every line would be noise, and a blank one would be a gap.
        Assert.Contains("hidden", vm.FindMessage("#main", "plain")!.ReplyClass, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongQuoteIsCutRatherThanRepeated()
    {
        var vm = Room();
        vm.Append("#main", "root", new string('x', 400), 0, id: "long");
        vm.Append("#main", "dagger", "agreed", 0, id: "reply", replyTo: "long");

        var quote = vm.FindMessage("#main", "reply")!.ReplyText;
        output.WriteLine($"{quote.Length} chars");

        // Enough to recognise which line it was, not enough to say it twice.
        Assert.True(quote.Length < 120, $"the quote repeated the message ({quote.Length} chars)");
        Assert.EndsWith("…", quote, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteOfSomethingNotLoadedStillReadsAsAReply()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "yes to that", 0, id: "reply", replyTo: "long-since-scrolled-past");

        var row = vm.FindMessage("#main", "reply")!;

        // Named but off the top of the scrollback. Saying so beats dropping the thread silently:
        // the reader can at least tell this line is answering something.
        Assert.DoesNotContain("hidden", row.ReplyClass, StringComparison.Ordinal);
        Assert.Contains("earlier message", row.ReplyText, StringComparison.Ordinal);
    }

    [Fact]
    public void PagingInOlderHistoryFillsInTheQuotesItCompletes()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "the second one", 0, id: "reply", replyTo: "way-back");
        Assert.Contains("earlier message", vm.FindMessage("#main", "reply")!.ReplyText, StringComparison.Ordinal);

        vm.Prepend("#main", [("way-back", "root", "shall we ship it?", 0L, "")]);

        // The quoted message is older than the reply, so it arrives after it. A quote that stayed
        // vague once its subject was on screen would be a thread the client could see and would
        // not show.
        var row = vm.FindMessage("#main", "reply")!;
        output.WriteLine(row.ReplyText);
        Assert.Contains("shall we ship it?", row.ReplyText, StringComparison.Ordinal);
    }

    // ── The composer ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheComposerSaysWhoItIsReplyingTo()
    {
        var vm = Room();
        vm.BeginReply("asked");

        // A mode you find out about by pressing Enter is not a mode anybody chose.
        Assert.Equal("asked", vm.ReplyingTo);
        Assert.Contains("replying to root", vm.Model.ReplyingText, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", vm.Model.ReplyingClass, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellingAReplyKeepsTheWords()
    {
        var vm = Room();
        vm.Model.Composer = "half a sentence";
        vm.BeginReply("asked");
        vm.ClearReply();

        // Changing your mind about who you are answering is not the same as changing your mind
        // about what you were going to say.
        Assert.Equal("", vm.ReplyingTo);
        Assert.Equal("half a sentence", vm.Model.Composer);
    }

    [Fact]
    public void SendingWhileReplyingNamesTheMessage()
    {
        var vm = Room();
        var replies = new List<(string Room, string Text, string ReplyTo)>();
        var plain = new List<string>();

        var app = new BanterChatApp(vm)
        {
            SendAsync = (_, text) => { plain.Add(text); return Task.CompletedTask; },
            ReplyAsync = (room, text, replyTo) => { replies.Add((room, text, replyTo)); return Task.CompletedTask; },
        };

        vm.BeginReply("asked");
        vm.Model.Composer = "the second one";
        app.Send();

        Assert.Empty(plain);
        Assert.Equal(("#main", "the second one", "asked"), Assert.Single(replies));

        // And the mode ends with the message: the next thing typed is a new remark, not a second
        // answer to a question that has been answered.
        Assert.Equal("", vm.ReplyingTo);
    }

    [Fact]
    public void SendingWithoutReplyingIsStillJustAMessage()
    {
        var vm = Room();
        var replies = new List<string>();
        var plain = new List<string>();

        var app = new BanterChatApp(vm)
        {
            SendAsync = (_, text) => { plain.Add(text); return Task.CompletedTask; },
            ReplyAsync = (_, text, _) => { replies.Add(text); return Task.CompletedTask; },
        };

        vm.Model.Composer = "morning all";
        app.Send();

        Assert.Empty(replies);
        Assert.Equal("morning all", Assert.Single(plain));
    }

    [Fact]
    public void TypingAnAnswerToAQuestionSendsAnAnswerAndNotAMessage()
    {
        var vm = Room();
        vm.Append("#main", "scribe", "[asking] may I bring in a web agent?", 0, id: "question");
        vm.AddAsk(new AskPayload("#main", "ask-1",
            [new AskQuestion("extra", "Extra agent", "May I bring in a web agent?",
                [new AskOption("yes", "Yes"), new AskOption("no", "No")])],
            "scribe", "question"));

        var answers = new List<AnswerPayload>();
        var replies = new List<string>();
        var app = new BanterChatApp(vm)
        {
            ReplyAsync = (_, text, _) => { replies.Add(text); return Task.CompletedTask; },
            AnswerAsync = a => { answers.Add(a); return Task.CompletedTask; },
        };

        vm.PickAskOption("ask-1|extra|no");
        vm.BeginReply("question", answering: true);
        vm.Model.Composer = "not yet — ask me again after the review";
        app.Send();

        // The agent is waiting on an ANSWER. Sent as an ordinary reply it would reach the room
        // and the agent would sit there until its timeout, which is the failure this exists to
        // prevent.
        Assert.Empty(replies);
        var said = Assert.Single(Assert.Single(answers).Answers);
        output.WriteLine($"{string.Join(",", said.Chosen)} / {said.Text}");

        Assert.Equal(["no"], said.Chosen);
        Assert.Equal("not yet — ask me again after the review", said.Text);
    }
}
