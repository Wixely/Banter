using Banter.App;
using Banter.Protocol;
using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// An agent's question, rendered on the message that asked it.
///
/// <para>The shape is the feature. A modal would stop the whole client to serve one agent's
/// question while other agents are working in the room and other people are reading it, so the
/// controls hang off the message: the room carries on, anyone can answer, and the question is
/// still there an hour later. Half of these tests exist to hold that shape in place.</para>
/// </summary>
public sealed class AskPanelTests(ITestOutputHelper output)
{
    private const int Width = 1240;
    private const int Height = 800;

    private const string AskId = "ask-1";
    private const string MessageId = "msg-1";

    private static AskPayload OneQuestion(bool multi = false, bool allowText = true) =>
        new("#main", AskId,
            [
                new AskQuestion("extra", "Extra agent", "This needs web search. May I bring one in?",
                    [
                        new AskOption("yes", "Yes", "Add an agent that can search."),
                        new AskOption("no", "No", "Carry on without."),
                    ],
                    MultiSelect: multi, AllowText: allowText),
            ],
            "scribe", MessageId);

    private static AskPayload TwoQuestions() =>
        new("#main", AskId,
            [
                new AskQuestion("who", "Who", "Which agent takes the database slice?",
                    [new AskOption("dagger", "dagger"), new AskOption("scout", "scout")]),
                new AskQuestion("when", "When", "Before or after the review?",
                    [new AskOption("before", "Before"), new AskOption("after", "After")]),
            ],
            "scribe", MessageId);

    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("nell");
        vm.AddRoom("#main");
        vm.Append("#main", "scribe", "[asking] This needs web search. May I bring one in?", 0, id: MessageId);
        return vm;
    }

    private static MessageRow Asked(ChatViewModel vm) => vm.FindMessage("#main", MessageId)!;

    // ── It is part of the message, not a window over it ──────────────────────────────────────

    [Fact]
    public void TheQuestionRendersOnTheMessageThatAskedIt()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion());

        var row = Asked(vm);
        output.WriteLine($"{row.AskHeader}: {row.AskText} [{string.Join(", ", row.AskOptions.Select(o => o.Label))}]");

        Assert.Equal("Extra agent", row.AskHeader);
        Assert.Equal(["Yes", "No"], row.AskOptions.Select(o => o.Label));
        Assert.DoesNotContain("hidden", row.AskClass, StringComparison.Ordinal);

        // And it really is inside the timeline row rather than floating over the window: every
        // other message in this room is still laid out and still hittable.
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var options = Walk(doc.Root)
            .Where(n => (n.Node.Element?.GetAttribute("data-ask-pick") ?? "").Length > 0)
            .ToList();

        Assert.Equal(2, options.Count);
        Assert.All(options, o => Assert.True(o.Node.Width > 0 && o.Node.Height > 0));

        // Inside the message, not over it: the composer below is still there to be typed into.
        var composer = Walk(doc.Root).First(n => n.Node.Element?.GetAttribute("data-composer") is not null);
        Assert.True(composer.Node.Height > 0, "the composer was covered by the question");
        Assert.True(composer.Top > options[0].Top, "the question was not above the composer");
    }

    [Fact]
    public void NothingIsShownOnAnOrdinaryMessage()
    {
        var vm = Room();
        vm.Append("#main", "nell", "just chatting", 0, id: "msg-2");

        var plain = vm.FindMessage("#main", "msg-2")!;
        Assert.Contains("hidden", plain.AskClass, StringComparison.Ordinal);
        Assert.Empty(plain.AskOptions);
    }

    // ── Choosing ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OneChoiceReplacesTheLast()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion());

        vm.PickAskOption($"{AskId}|extra|yes");
        vm.PickAskOption($"{AskId}|extra|no");

        var row = Asked(vm);
        output.WriteLine(string.Join(" ", row.AskOptions.Select(o => $"{o.Mark}{o.Label}")));

        // A single-choice question that let two answers stand would be sending an agent a
        // contradiction and calling it a decision.
        Assert.Equal("choose one", row.AskHint);
        Assert.Equal([false, true], row.AskOptions.Select(o => o.RowClass.Contains("chosen", StringComparison.Ordinal)));

        var answer = vm.BuildAnswer(AskId, "")!;
        Assert.Equal(["no"], Assert.Single(answer.Answers).Chosen);
    }

    [Fact]
    public void SeveralChoicesStandTogetherWhenTheQuestionAllowsIt()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion(multi: true));

        vm.PickAskOption($"{AskId}|extra|yes");
        vm.PickAskOption($"{AskId}|extra|no");

        var row = Asked(vm);
        Assert.Equal("choose any", row.AskHint);

        // The glyph says which kind of question it is before anything is clicked, rather than
        // leaving it to be discovered when a second click makes the first answer vanish.
        Assert.All(row.AskOptions, o => Assert.Equal("☑", o.Mark));

        var answer = vm.BuildAnswer(AskId, "")!;
        Assert.Equal(["yes", "no"], Assert.Single(answer.Answers).Chosen);
    }

    [Fact]
    public void ChoosingTheChosenOneTakesItBack()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion());

        vm.PickAskOption($"{AskId}|extra|yes");
        vm.PickAskOption($"{AskId}|extra|yes");

        // Otherwise a misplaced click is a decision with no way out of it.
        Assert.Null(vm.BuildAnswer(AskId, ""));
        Assert.Contains("idle", Asked(vm).AskSendClass, StringComparison.Ordinal);
    }

    // ── More than one question ───────────────────────────────────────────────────────────────

    [Fact]
    public void SeveralQuestionsBecomeTabsAndOnlyOneShowsAtATime()
    {
        var vm = Room();
        vm.AddAsk(TwoQuestions());

        var row = Asked(vm);
        output.WriteLine(string.Join(" | ", row.AskTabs.Select(t => $"{t.Label}:{t.TabClass}")));

        Assert.Equal(["Who", "When"], row.AskTabs.Select(t => t.Label));
        Assert.DoesNotContain("hidden", row.AskTabsClass, StringComparison.Ordinal);

        // One question's options, not all of them: two decisions stacked in one panel read as
        // one decision with four answers.
        Assert.Equal(["dagger", "scout"], row.AskOptions.Select(o => o.Label));

        vm.SelectAskTab($"{AskId}|when");
        row = Asked(vm);
        Assert.Equal(["Before", "After"], row.AskOptions.Select(o => o.Label));
    }

    [Fact]
    public void ASingleQuestionGetsNoTabs()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion());

        // One tab is a label pretending to be a control.
        Assert.Contains("hidden", Asked(vm).AskTabsClass, StringComparison.Ordinal);
    }

    [Fact]
    public void AnswersToEveryTabAreSentTogether()
    {
        var vm = Room();
        vm.AddAsk(TwoQuestions());

        vm.PickAskOption($"{AskId}|who|dagger");
        vm.SelectAskTab($"{AskId}|when");
        vm.PickAskOption($"{AskId}|when|after");

        var answer = vm.BuildAnswer(AskId, "")!;
        output.WriteLine(string.Join("; ", answer.Answers.Select(a => $"{a.Key}={string.Join(",", a.Chosen)}")));

        // The agent asked two things at once because they are one decision in parts, so half an
        // answer would leave it waiting for the rest of something it will never be sent.
        Assert.Equal(2, answer.Answers.Count);
        Assert.Equal(["dagger"], answer.Answers.First(a => a.Key == "who").Chosen);
        Assert.Equal(["after"], answer.Answers.First(a => a.Key == "when").Chosen);
    }

    [Fact]
    public void ATabThatHasBeenAnsweredSaysSo()
    {
        var vm = Room();
        vm.AddAsk(TwoQuestions());
        vm.PickAskOption($"{AskId}|who|dagger");
        vm.SelectAskTab($"{AskId}|when");

        var tabs = Asked(vm).AskTabs;
        output.WriteLine(string.Join(" | ", tabs.Select(t => $"{t.Label}:{t.TabClass}")));

        // Without this, the only way to find out whether the other tab still needs an answer is
        // to click it and look.
        Assert.Contains("done", tabs.First(t => t.Label == "Who").TabClass, StringComparison.Ordinal);
        Assert.Contains("active", tabs.First(t => t.Label == "When").TabClass, StringComparison.Ordinal);
    }

    // ── Writing something the buttons did not offer ──────────────────────────────────────────

    [Fact]
    public void WhatIsTypedRidesAlongWithWhatWasClicked()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion());
        vm.PickAskOption($"{AskId}|extra|no");

        var answer = vm.BuildAnswer(AskId, "  not yet — ask me again after the review  ")!;
        var said = Assert.Single(answer.Answers);
        output.WriteLine($"{string.Join(",", said.Chosen)} / {said.Text}");

        // Both. Somebody who wrote something wrote it because the buttons did not say what they
        // meant, and dropping either half loses half of what they said.
        Assert.Equal(["no"], said.Chosen);
        Assert.Equal("not yet — ask me again after the review", said.Text);
    }

    [Fact]
    public void TypingAloneIsAnAnswer()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion());

        var answer = vm.BuildAnswer(AskId, "neither — do the other thing first")!;
        Assert.Equal("neither — do the other thing first", Assert.Single(answer.Answers).Text);
    }

    [Fact]
    public void TextBelongsToTheQuestionOnScreenAndNotToAllOfThem()
    {
        var vm = Room();
        vm.AddAsk(TwoQuestions());
        vm.SelectAskTab($"{AskId}|when");

        var answer = vm.BuildAnswer(AskId, "whenever dagger is free")!;
        var said = Assert.Single(answer.Answers);

        // The same sentence against three different decisions is three wrong answers.
        Assert.Equal("when", said.Key);
    }

    [Fact]
    public void AQuestionThatWantsOptionsOnlySaysSo()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion(allowText: false));

        Assert.Contains("hidden", Asked(vm).AskWriteClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TheComposerSaysWhichQuestionItIsAnswering()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion());
        vm.BeginReply(MessageId, answering: true);

        output.WriteLine($"{vm.Model.ReplyingText} / {Asked(vm).AskWrite}");

        // A mode you find out about by pressing Enter is not a mode anyone chose.
        Assert.Contains("answering scribe", vm.Model.ReplyingText, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", vm.Model.ReplyingClass, StringComparison.Ordinal);
        Assert.Contains("typing an answer", Asked(vm).AskWrite, StringComparison.Ordinal);
    }

    // ── Once it is over ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnsweringItTakesTheControlsAwayAndLeavesTheDecision()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion());
        vm.CloseAsk(AskId, "nell");

        var row = Asked(vm);
        output.WriteLine($"{row.AskClass} — {row.AskHint}");

        // Kept in place rather than removed: what was decided is part of the conversation, and a
        // question that vanishes leaves a reply above it answering nothing.
        Assert.Empty(row.AskOptions);
        Assert.Contains("answered", row.AskClass, StringComparison.Ordinal);
        Assert.Contains("nell", row.AskHint, StringComparison.Ordinal);
        Assert.False(vm.IsAskOpen(AskId));
    }

    [Fact]
    public void SomebodyElseAnsweringFirstClosesItHereToo()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion());
        vm.PickAskOption($"{AskId}|extra|yes");

        vm.CloseAsk(AskId, "root");

        // Two people answering the same question is not a conflict worth resolving; it is one
        // that should not arise.
        Assert.Null(vm.BuildAnswer(AskId, "yes"));
    }

    [Fact]
    public void TheComposerStopsAnsweringAQuestionThatIsOver()
    {
        var vm = Room();
        vm.AddAsk(OneQuestion());
        vm.BeginReply(MessageId, answering: true);

        vm.CloseAsk(AskId, "root");

        // Otherwise the next thing typed is sent as an answer to something already settled.
        Assert.Equal("", vm.ReplyingTo);
        Assert.Contains("hidden", vm.Model.ReplyingClass, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuestionSurvivesLeavingTheRoomAndComingBack()
    {
        var vm = Room();
        vm.AddRoom("#other");
        vm.AddAsk(OneQuestion());
        vm.PickAskOption($"{AskId}|extra|yes");

        vm.SwitchTo("#other");
        vm.SwitchTo("#main");

        // The rows are rebuilt on a room switch, so an ask that did not reattach would be a
        // question the agent is still waiting on that nobody can see any more.
        var row = Asked(vm);
        Assert.DoesNotContain("hidden", row.AskClass, StringComparison.Ordinal);
        Assert.Equal("Yes", row.AskOptions.Single(o => o.RowClass.Contains("chosen", StringComparison.Ordinal)).Label);
    }

    private static IEnumerable<(RenderNode Node, float Left, float Top)> Walk(
        RenderNode node, float parentLeft = 0, float parentTop = 0)
    {
        var left = parentLeft + node.X;
        var top = parentTop + node.Y;
        yield return (node, left, top);
        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child, left, top))
            {
                yield return descendant;
            }
        }
    }
}
