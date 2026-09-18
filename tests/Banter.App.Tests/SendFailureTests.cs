using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// A message that did not reach the server says so, and gives the words back.
///
/// <para>Found on a phone: Android destroys a backgrounded app's sockets, and a message typed on
/// returning was sent at a connection that was already being replaced. Every caller starts the
/// send with a discard, so the failure had nobody to throw to — the composer had already been
/// cleared, and the room showed no trace of either the message or the problem. The words were
/// simply gone.</para>
/// </summary>
public sealed class SendFailureTests
{
    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        return vm;
    }

    [Fact]
    public void AFailedSendIsSaidOutLoudInTheRoom()
    {
        var vm = Room();

        vm.SendFailed("#main", "the one that got away", "The connection to the server was lost.");

        var last = vm.Model.Messages[^1];
        Assert.Equal("*", last.Sender);
        Assert.Contains("not sent", last.Text);
        Assert.Contains("The connection to the server was lost.", last.Text);
    }

    [Fact]
    public void TheWordsGoBackIntoTheComposer()
    {
        var vm = Room();
        Assert.Equal("", vm.Model.Composer);

        vm.SendFailed("#main", "the one that got away", "gone");

        Assert.Equal("the one that got away", vm.Model.Composer);
    }

    [Fact]
    public void ADraftTypedSinceIsNotOverwritten()
    {
        var vm = Room();
        vm.Model.Composer = "something else entirely";

        vm.SendFailed("#main", "the one that got away", "gone");

        // Restoring here would lose as much as it recovered, and the failure is reported either
        // way.
        Assert.Equal("something else entirely", vm.Model.Composer);
        Assert.Contains("not sent", vm.Model.Messages[^1].Text);
    }
}
