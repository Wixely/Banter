using Banter.App;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// A turn that failed reads as a chip, not as a paragraph of somebody else's plumbing.
///
/// <para>An agent that cannot reach its model used to paste the whole reason into the room -
/// "(dagger failed to answer: Retry failed after 4 tries)" - across the middle of a conversation,
/// which on a phone is most of a screen to say that something did not work. The chip says Failed
/// and the reason is one tap behind it.</para>
/// </summary>
public sealed class FailedTurnTests(ITestOutputHelper output)
{
    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.Connected("tcp://h:7770", "alice");
        return vm;
    }

    [Fact]
    public void TheMarkerBecomesAChipWithTheReasonBehindIt()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "[failed] Retry failed after 4 tries", 0, id: "m1");

        var row = vm.Model.Messages.Last();
        output.WriteLine($"text='{row.Text}' chip='{row.FailClass}' detail='{row.FailDetail}'");

        // The room is not made to read the reason it did not ask for.
        Assert.Equal("", row.Text);
        Assert.DoesNotContain("hidden", row.FailClass, StringComparison.Ordinal);
        Assert.Equal("Retry failed after 4 tries", row.FailDetail);
        Assert.Contains("hidden", row.FailDetailClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TappingItOpensTheReasonAndTappingAgainPutsItAway()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "[failed] the model said no", 0, id: "m1");

        vm.ToggleFailure("m1");
        Assert.DoesNotContain("hidden", vm.Model.Messages.Last().FailDetailClass, StringComparison.Ordinal);

        vm.ToggleFailure("m1");
        Assert.Contains("hidden", vm.Model.Messages.Last().FailDetailClass, StringComparison.Ordinal);
    }

    /// <summary>One room, two failures: opening one does not open the other. Opening all of them
    /// to read one would be the wall of text this replaced.</summary>
    [Fact]
    public void OneChipOpensAtATime()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "[failed] first", 0, id: "m1");
        vm.Append("#main", "scribe", "[failed] second", 0, id: "m2");

        vm.ToggleFailure("m1");

        var first = vm.Model.Messages.First(m => m.Id == "m1");
        var second = vm.Model.Messages.First(m => m.Id == "m2");
        Assert.DoesNotContain("hidden", first.FailDetailClass, StringComparison.Ordinal);
        Assert.Contains("hidden", second.FailDetailClass, StringComparison.Ordinal);
    }

    /// <summary>
    /// The prose an older agent sends still reads as a chip. A room is routinely a mix of
    /// versions, and that is the normal state of one rather than the exception.
    /// </summary>
    [Fact]
    public void AnOlderAgentsSentenceIsUnderstoodToo()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "(dagger failed to answer: Retry failed after 4 tries)", 0, id: "m1");

        var row = vm.Model.Messages.Last();
        output.WriteLine($"text='{row.Text}' detail='{row.FailDetail}'");

        Assert.Equal("", row.Text);
        Assert.Equal("Retry failed after 4 tries", row.FailDetail);
    }

    /// <summary>A message that merely mentions failing is not one. The marker and the sentence are
    /// specific shapes, and anything else is somebody talking.</summary>
    [Theory]
    [InlineData("that failed to answer the question, I think")]
    [InlineData("[tool] gh_list_issues")]
    [InlineData("the build failed")]
    public void OrdinaryTalkIsLeftAlone(string said)
    {
        var vm = Room();
        vm.Append("#main", "bob", said, 0, id: "m1");

        var row = vm.Model.Messages.Last();
        Assert.Equal(said, row.Text);
        Assert.Contains("hidden", row.FailClass, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no id there is nothing to toggle with, so the reason is shown rather than hidden
    /// behind a control that cannot work.
    /// </summary>
    [Fact]
    public void WithoutAnIdTheReasonIsSimplyShown()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "[failed] nothing to tap", 0);

        var row = vm.Model.Messages.Last();
        Assert.DoesNotContain("hidden", row.FailDetailClass, StringComparison.Ordinal);
    }
}
