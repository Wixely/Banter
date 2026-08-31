using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// What the timeline does when a message is changed or taken back. The rules live on the server;
/// these are about what a reader is shown, which is where the honesty of the feature lands.
/// </summary>
public sealed class EditAndDeleteTimelineTests
{
    private static ChatViewModel InRoom(string nick = "alice")
    {
        var vm = new ChatViewModel();
        vm.SetNick(nick);
        vm.AddRoom("#main");
        return vm;
    }

    [Fact]
    public void AnEditReplacesTheWordsAndSaysSo()
    {
        var vm = InRoom();
        vm.Append("#main", "alice", "teh cat sat", 0, id: "m1");

        vm.MarkEdited("#main", "m1", "the cat sat");

        var row = vm.Model.Messages.Single(r => r.Id == "m1");
        Assert.Equal("the cat sat", row.Text);
        // Somebody may have already read it, or replied to it. Swapping the words with no mark
        // would leave that reader holding a conversation nobody else can see.
        Assert.Contains("edited", row.EditedMark, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletionTakesTheWordsOffTheScreen()
    {
        var vm = InRoom();
        vm.Append("#main", "alice", "something regrettable", 0, id: "m1");

        vm.MarkDeleted("#main", "m1");

        var row = vm.Model.Messages.Single(r => r.Id == "m1");
        // Not greyed out and still readable: the words are gone from the server, so leaving them
        // on screen would show exactly what was asked to be removed.
        Assert.DoesNotContain("regrettable", row.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deleted", row.RowClass, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletedMessageKeepsItsPlaceInTheConversation()
    {
        var vm = InRoom();
        vm.Append("#main", "bob", "what do you think?", 0, id: "m1");
        vm.Append("#main", "alice", "regrettable", 0, id: "m2");
        vm.Append("#main", "bob", "steady on", 0, id: "m3");

        vm.MarkDeleted("#main", "m2");

        // The row stays where it was: removing it would make bob's reply look like an answer to
        // the question above it.
        Assert.Equal(["m1", "m2", "m3"], vm.Model.Messages.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void DeletingAMessageTakesItsAttachmentWithIt()
    {
        var vm = InRoom();
        vm.Append("#main", "alice", "here it is", 0, id: "m1", fileId: "f1");
        vm.SetAttachmentInfo("f1", "receipt.png", 1024);

        vm.MarkDeleted("#main", "m1");

        var row = vm.Model.Messages.Single(r => r.Id == "m1");
        Assert.Equal("", row.FileId);
        Assert.Contains("hidden", row.AttachClass, StringComparison.Ordinal);
        Assert.Contains("hidden", row.ImageClass, StringComparison.Ordinal);
    }

    [Fact]
    public void EditsForMessagesWeDoNotHaveAreIgnored()
    {
        var vm = InRoom();
        vm.Append("#main", "alice", "only message", 0, id: "m1");

        // Scrollback is finite, so an edit can arrive for a message that has aged out. Ignoring it
        // is right; throwing would take the timeline down over a message nobody can see.
        vm.MarkEdited("#main", "gone", "new text");
        vm.MarkDeleted("#main", "gone");

        Assert.Equal("only message", vm.Model.Messages.Single().Text);
    }

    [Fact]
    public void TheLastOwnMessageIsWhatTheCommandsActOn()
    {
        var vm = InRoom("alice");
        vm.Append("#main", "alice", "first", 0, id: "m1");
        vm.Append("#main", "bob", "not mine", 0, id: "m2");
        vm.Append("#main", "alice", "second", 0, id: "m3");

        Assert.Equal("m3", vm.LastOwnMessageId("#main"));
    }

    [Fact]
    public void AlreadyDeletedMessagesAreSkippedWhenLookingBack()
    {
        var vm = InRoom("alice");
        vm.Append("#main", "alice", "first", 0, id: "m1");
        vm.Append("#main", "alice", "second", 0, id: "m2");
        vm.MarkDeleted("#main", "m2");

        // Otherwise /delete twice would report an error on something already gone instead of
        // reaching the message before it.
        Assert.Equal("m1", vm.LastOwnMessageId("#main"));
    }

    [Fact]
    public void WithNothingOfOursThereIsNothingToActOn()
    {
        var vm = InRoom("alice");
        vm.Append("#main", "bob", "bob's message", 0, id: "m1");

        Assert.Equal("", vm.LastOwnMessageId("#main"));
    }
}
