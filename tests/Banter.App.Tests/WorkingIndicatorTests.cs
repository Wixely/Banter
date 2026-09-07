using Banter.App;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The row an agent's reply grows into, while it has nothing in it yet.
///
/// <para>An empty bubble carrying a name and a timestamp reads as a message that failed to
/// arrive, not one being written — and the gap is not brief, because a delegated job spends it
/// choosing a model, waiting on a tool, or handing off to another agent.</para>
/// </summary>
public sealed class WorkingIndicatorTests
{
    /// <summary>
    /// The dots are CSS keyframes, so "is it animating" is a question the engine can answer.
    /// Asserted because a class that is set and a screen that moves are different things, and
    /// the difference here is one @keyframes block nobody would miss by reading.
    /// </summary>
    [Fact]
    public void TheDotsActuallyAnimateAndStop()
    {
        var vm = Room();
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);
        app.Present(1100, 760);
        Assert.False(doc.HasActiveAnimations);

        // Posted, not called: Present only rebinds the document when there were queued
        // mutations, which is exactly how BanterChatSession delivers a stream.
        vm.Post(() => vm.StreamStart("#main", "x16", "s1"));
        app.Present(1100, 760);
        doc.BuildDisplayList(1100, 760);
        Assert.True(doc.HasActiveAnimations);

        vm.Post(() => vm.StreamEnd("s1", "done", 0));
        app.Present(1100, 760);
        doc.BuildDisplayList(1100, 760);
        Assert.False(doc.HasActiveAnimations);
    }

    /// <summary>
    /// The working row is still a row. The first version of this styled the dots with a bare
    /// <c>.working</c> rule, which also matched the row - the row carries <c>working</c> as its
    /// state - so <c>display: none</c> hid the entire message: no avatar, no name, no dots.
    /// Counting rendered rows is what caught it, because a screenshot of a hidden thing looks
    /// like a screenshot of a thing that has not arrived yet.
    /// </summary>
    [Fact]
    public void AWorkingRowIsStillDrawn()
    {
        var vm = Room();
        vm.SetNick("alice");
        vm.Append("#main", "x16", "", 0, "line streaming working");

        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);
        app.Present(1100, 760);
        doc.BuildDisplayList(1100, 760);

        var dump = doc.DebugDump(1100, 760);
        Assert.Contains("msg-main", dump, StringComparison.Ordinal);
        Assert.Contains("wdot", dump, StringComparison.Ordinal);
    }

    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        return vm;
    }

    private static MessageRow Only(ChatViewModel vm) => Assert.Single(vm.Model.Messages);

    [Fact]
    public void AnAgentAskedButSilentIsShownWorking()
    {
        var vm = Room();
        vm.StreamStart("#main", "x16", "s1");

        var row = Only(vm);
        Assert.Contains("working", row.RowClass, StringComparison.Ordinal);
        Assert.Equal("", row.Text);
    }

    [Fact]
    public void TheFirstRealTokenReplacesIt()
    {
        var vm = Room();
        vm.StreamStart("#main", "x16", "s1");
        vm.StreamDelta("s1", "Looking");

        var row = Only(vm);
        Assert.DoesNotContain("working", row.RowClass, StringComparison.Ordinal);
        Assert.Contains("streaming", row.RowClass, StringComparison.Ordinal);
        Assert.Equal("Looking", row.Text);
    }

    [Fact]
    public void WhitespaceIsNotAToken()
    {
        // A model that opens with a newline is common, and dropping the animation for it would
        // blank the row rather than replace the dots with words.
        var vm = Room();
        vm.StreamStart("#main", "x16", "s1");
        vm.StreamDelta("s1", "");
        vm.StreamDelta("s1", "\n");
        vm.StreamDelta("s1", "  ");

        Assert.Contains("working", Only(vm).RowClass, StringComparison.Ordinal);

        vm.StreamDelta("s1", "Right");
        Assert.DoesNotContain("working", Only(vm).RowClass, StringComparison.Ordinal);
    }

    [Fact]
    public void AReplyThatEndsWithoutSayingAnythingStopsWorking()
    {
        // A refusal, a tool that failed, an agent that dropped. Without this the room shows
        // something working away at nothing for as long as it is left open.
        var vm = Room();
        vm.StreamStart("#main", "x16", "s1");
        vm.StreamEnd("s1", "", 0);

        var row = Only(vm);
        Assert.DoesNotContain("working", row.RowClass, StringComparison.Ordinal);
        Assert.DoesNotContain("streaming", row.RowClass, StringComparison.Ordinal);
    }

    [Fact]
    public void EndingReplacesTheDeltasAndStopsWorking()
    {
        var vm = Room();
        vm.StreamStart("#main", "x16", "s1");
        vm.StreamDelta("s1", "partial");
        vm.StreamEnd("s1", "the whole answer", 1_700_000_000);

        var row = Only(vm);
        Assert.Equal("the whole answer", row.Text);
        Assert.DoesNotContain("working", row.RowClass, StringComparison.Ordinal);
        Assert.DoesNotContain("streaming", row.RowClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoAgentsWorkAtOnceWithoutOneStoppingTheOther()
    {
        // A delegator opens a side room and two agents answer at once; the first to speak must
        // not take the other's animation away with it.
        var vm = Room();
        vm.StreamStart("#main", "scout", "s1");
        vm.StreamStart("#main", "dagger", "s2");

        vm.StreamDelta("s1", "found it");

        var rows = vm.Model.Messages;
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain("working", rows[0].RowClass, StringComparison.Ordinal);
        Assert.Contains("working", rows[1].RowClass, StringComparison.Ordinal);
    }
}
