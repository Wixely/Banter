using Banter.App;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Marking messages whose author has left the room.
///
/// <para>A room's backlog outlives the people in it, and the difference matters here more than in
/// an ordinary chat: the reply somebody is waiting for may be from an agent that is no longer in
/// the room to give it.</para>
/// </summary>
public sealed class PresenceTests(ITestOutputHelper output)
{
    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        return vm;
    }

    private static string ClassOf(ChatViewModel vm, string sender) =>
        vm.Model.Messages.Last(m => m.Sender == sender).RowClass;

    [Fact]
    public void NobodyIsMarkedUntilTheRosterHasBeenRead()
    {
        var vm = Room();
        vm.Append("#main", "bob", "hello", 0);

        // History arrives before the roster does. Greying every name for that moment would look
        // like the room was broken rather than like information.
        output.WriteLine(ClassOf(vm, "bob"));
        Assert.DoesNotContain("away", ClassOf(vm, "bob"), StringComparison.Ordinal);
    }

    [Fact]
    public void SomebodyWhoLeftHasWhatTheyAlreadySaidMarkedToo()
    {
        var vm = Room();
        vm.SetRoomUsers("#main", [("alice", ""), ("bob", "")]);
        vm.Append("#main", "bob", "back in a minute", 0);
        Assert.DoesNotContain("away", ClassOf(vm, "bob"), StringComparison.Ordinal);

        // Bob leaves. Marking only messages that arrive AFTER would be the difference between
        // "who is here" and "who was here when this arrived" — the wrong one of the two.
        vm.SetRoomUsers("#main", [("alice", "")]);

        output.WriteLine(ClassOf(vm, "bob"));
        Assert.Contains("away", ClassOf(vm, "bob"), StringComparison.Ordinal);
    }

    [Fact]
    public void ComingBackClearsTheMark()
    {
        var vm = Room();
        vm.SetRoomUsers("#main", [("alice", ""), ("bob", "")]);
        vm.Append("#main", "bob", "hello", 0);
        vm.SetRoomUsers("#main", [("alice", "")]);
        Assert.Contains("away", ClassOf(vm, "bob"), StringComparison.Ordinal);

        vm.SetRoomUsers("#main", [("alice", ""), ("bob", "")]);
        Assert.DoesNotContain("away", ClassOf(vm, "bob"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentThatLeftIsMarkedTheSameWayAPersonIs()
    {
        var vm = Room();
        vm.SetRoomUsers("#main", [("alice", "")]);
        vm.SetAgents("#main", [("dagger", true, "chat", true)]);
        vm.Append("#main", "dagger", "on it", 0);
        Assert.DoesNotContain("away", ClassOf(vm, "dagger"), StringComparison.Ordinal);

        // The agent whose answer you are waiting for going quiet is exactly the case this is for.
        vm.SetAgents("#main", []);
        Assert.Contains("away", ClassOf(vm, "dagger"), StringComparison.Ordinal);
    }

    [Fact]
    public void YourOwnMessagesAndSystemLinesAreNeverMarked()
    {
        var vm = Room();
        vm.SetRoomUsers("#main", []);          // roster read, and it lists nobody at all
        vm.Append("#main", "alice", "mine", 0);
        vm.System("#main", "something happened");

        // Yours are yours whether or not the room still lists you, and a system line has no
        // author to have left.
        Assert.DoesNotContain("away", ClassOf(vm, "alice"), StringComparison.Ordinal);
        Assert.DoesNotContain("away", ClassOf(vm, "*"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkSurvivesTheOtherThingsARowClassCarries()
    {
        var vm = Room();
        vm.SetRoomUsers("#main", [("alice", "")]);
        vm.Append("#main", "dagger", "[egress] sending this to claude", 0);

        // Row classes stack — egress, streaming, deleted, own. Absence has to compose with them
        // rather than replace them, or an egress notice from an agent that left stops looking
        // like an egress notice.
        var cls = ClassOf(vm, "dagger");
        output.WriteLine(cls);
        Assert.Contains("egress", cls, StringComparison.Ordinal);
        Assert.Contains("away", cls, StringComparison.Ordinal);
    }
}
