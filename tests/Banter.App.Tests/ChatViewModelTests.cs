using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The client's behaviour, with no document, window or server involved — which is the point of
/// keeping <see cref="ChatViewModel"/> free of CupriFace.
/// </summary>
public sealed class ChatViewModelTests
{
    private static ChatViewModel Joined(string room = "#main", string nick = "alice")
    {
        var vm = new ChatViewModel();
        vm.SetNick(nick);
        vm.AddRoom(room);
        return vm;
    }

    [Fact]
    public void JoiningTheFirstRoomMakesItActive()
    {
        var vm = Joined();

        Assert.Equal("#main", vm.Model.ActiveRoom);
        Assert.Equal("tab active", Assert.Single(vm.Model.Rooms).TabClass);
    }

    [Fact]
    public void MessagesFromOthersAndSelfAreStyledDifferently()
    {
        var vm = Joined();

        vm.Append("#main", "bob", "hello", 0);
        vm.Append("#main", "alice", "hi", 0);

        Assert.Equal("line", vm.Model.Messages[0].RowClass);
        Assert.Equal("line own", vm.Model.Messages[1].RowClass);
    }

    [Fact]
    public void MessagesForAnInactiveRoomBadgeItInsteadOfLeakingIntoTheView()
    {
        var vm = Joined();
        vm.AddRoom("#other");

        vm.Append("#other", "bob", "psst", 0);

        Assert.Empty(vm.Model.Messages);
        Assert.Equal("1", vm.Model.Rooms.Single(r => r.Name == "#other").Badge);
        Assert.Single(vm.Backlog("#other"));
    }

    [Fact]
    public void SwitchingRoomsShowsThatRoomsBacklogAndClearsItsBadge()
    {
        var vm = Joined();
        vm.AddRoom("#other");
        vm.Append("#main", "bob", "in main", 0);
        vm.Append("#other", "carol", "in other", 0);

        vm.SwitchTo("#other");

        Assert.Equal("in other", Assert.Single(vm.Model.Messages).Text);
        Assert.Equal("", vm.Model.Rooms.Single(r => r.Name == "#other").Badge);

        // And back again — the first room's history survived the switch.
        vm.SwitchTo("#main");
        Assert.Equal("in main", Assert.Single(vm.Model.Messages).Text);
    }

    [Fact]
    public void StreamedMessageGrowsThenIsReplacedByTheAuthoritativeFinalText()
    {
        var vm = Joined();

        vm.StreamStart("#main", "dagger", "s1");
        vm.StreamDelta("s1", "Hel");
        vm.StreamDelta("s1", "lo");

        var row = Assert.Single(vm.Model.Messages);
        Assert.Equal("Hello", row.Text);
        Assert.Contains("streaming", row.RowClass);

        // The server's FinalText wins, so a dropped delta cannot corrupt the message.
        vm.StreamEnd("s1", "Hello, world", 1_700_000_000_000);

        Assert.Equal("Hello, world", row.Text);
        Assert.DoesNotContain("streaming", row.RowClass);
    }

    [Fact]
    public void DeltasForAnUnknownStreamAreIgnoredRatherThanThrowing()
    {
        var vm = Joined();

        vm.StreamDelta("never-started", "x");
        vm.StreamEnd("never-started", "x", 0);

        Assert.Empty(vm.Model.Messages);
    }

    [Fact]
    public void ScrollbackIsCappedSoALongLivedRoomCannotGrowWithoutBound()
    {
        var vm = new ChatViewModel { RoomScrollback = 10 };
        vm.AddRoom("#main");

        for (var i = 0; i < 25; i++)
        {
            vm.Append("#main", "bob", $"m{i}", 0);
        }

        Assert.Equal(10, vm.Model.Messages.Count);
        Assert.Equal("m24", vm.Model.Messages[^1].Text);
        Assert.Equal("m15", vm.Model.Messages[0].Text);
    }

    [Fact]
    public void PartingTheActiveRoomFallsBackToAnotherJoinedRoom()
    {
        var vm = Joined();
        vm.AddRoom("#other");
        vm.Append("#other", "bob", "still here", 0);

        vm.RemoveRoom("#main");

        Assert.Equal("#other", vm.Model.ActiveRoom);
        Assert.Equal("still here", Assert.Single(vm.Model.Messages).Text);
    }

    [Fact]
    public void MutationsQueuedFromOtherThreadsAreAppliedOnceAndInOrder()
    {
        var vm = Joined();

        // What actually happens at runtime: socket threads post, the render thread drains.
        Parallel.For(0, 200, i => vm.Post(() => vm.Append("#main", "bob", $"m{i}", 0)));

        Assert.Empty(vm.Model.Messages);          // nothing applied until the render thread asks
        Assert.True(vm.ApplyPending());
        Assert.Equal(200, vm.Model.Messages.Count);
        Assert.False(vm.ApplyPending());          // and the queue is drained, not replayed
    }
}
