using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// Paged scrollback: older history spliced above what is shown, without duplicating the live feed
/// and without the viewport jumping.
/// </summary>
public sealed class ScrollbackPagingTests
{
    private static ChatViewModel Room(string room = "#main")
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom(room);
        return vm;
    }

    private static List<(string, string, string, long)> Page(params string[] texts) =>
        texts.Select((t, i) => ($"id-{t}", "bob", t, (long)i)).ToList();

    [Fact]
    public void LoadOlderIsHiddenUntilTheServerSaysThereIsMore()
    {
        var vm = Room();

        Assert.Equal("loadmore hidden", vm.Model.LoadOlderClass);
        Assert.False(vm.CanLoadOlder("#main"));

        vm.SetHistoryCursor("#main", "cursor-1");

        Assert.Equal("loadmore", vm.Model.LoadOlderClass);
        Assert.True(vm.CanLoadOlder("#main"));
    }

    [Fact]
    public void ExhaustingHistoryHidesTheControlAgain()
    {
        var vm = Room();
        vm.SetHistoryCursor("#main", "cursor-1");

        vm.SetHistoryCursor("#main", null);

        Assert.Equal("loadmore hidden", vm.Model.LoadOlderClass);
        Assert.False(vm.CanLoadOlder("#main"));
    }

    [Fact]
    public void OlderMessagesGoAboveWhatIsAlreadyShown()
    {
        var vm = Room();
        vm.Append("#main", "bob", "recent", 0, id: "id-recent");

        vm.Prepend("#main", Page("older-1", "older-2"));

        Assert.Equal(["older-1", "older-2", "recent"], vm.Model.Messages.Select(m => m.Text));
    }

    [Fact]
    public void APageOverlappingTheLiveFeedDoesNotDuplicateMessages()
    {
        var vm = Room();
        // Arrived live while the history request was in flight — the classic overlap.
        vm.Append("#main", "bob", "seen", 0, id: "id-seen");

        var inserted = vm.Prepend("#main", Page("older", "seen"));

        Assert.Equal(1, inserted);
        Assert.Equal(["older", "seen"], vm.Model.Messages.Select(m => m.Text));
    }

    [Fact]
    public void PrependedCountIsReportedOnceSoTheScrollAnchorMovesExactlyOnce()
    {
        var vm = Room();
        vm.Append("#main", "bob", "recent", 0, id: "id-recent");

        vm.Prepend("#main", Page("a", "b", "c"));

        // The app hands this to VirtualListInserted before Refresh; double-counting would
        // scroll the viewport by twice the height of the inserted page.
        Assert.Equal(3, vm.TakePrependedCount());
        Assert.Equal(0, vm.TakePrependedCount());
    }

    [Fact]
    public void PrependingToAnInactiveRoomFillsItsBacklogWithoutMovingTheVisibleView()
    {
        var vm = Room();
        vm.AddRoom("#other");
        vm.Append("#other", "bob", "recent", 0, id: "id-recent");
        vm.SwitchTo("#main");

        var inserted = vm.Prepend("#other", Page("older"));

        // Backlog grew, but nothing was inserted into the visible list — so no anchor shift.
        Assert.Equal(0, inserted);
        Assert.Equal(0, vm.TakePrependedCount());
        Assert.Equal(2, vm.Backlog("#other").Count);
    }

    [Fact]
    public void SwitchingRoomsDiscardsAPendingPrependCountFromTheRoomWeLeft()
    {
        var vm = Room();
        vm.AddRoom("#other");
        vm.Append("#main", "bob", "recent", 0, id: "id-recent");
        vm.Prepend("#main", Page("a", "b"));

        // A room switch is a wholesale rebind; carrying the count over would anchor the new
        // room's list against rows it never received.
        vm.SwitchTo("#other");

        Assert.Equal(0, vm.TakePrependedCount());
    }

    [Fact]
    public void LoadingOlderIsNotUndoneByTheScrollbackCap()
    {
        var vm = new ChatViewModel { RoomScrollback = 5 };
        vm.SetNick("alice");
        vm.AddRoom("#main");
        for (var i = 0; i < 5; i++)
        {
            vm.Append("#main", "bob", $"m{i}", 0, id: $"id-{i}");
        }

        vm.Prepend("#main", Page("older-1", "older-2"));

        // The user just asked to see further back — trimming to the cap here would immediately
        // throw away what they requested.
        Assert.Equal(7, vm.Model.Messages.Count);
        Assert.Equal("older-1", vm.Model.Messages[0].Text);
    }

    [Fact]
    public void CursorsAreTrackedPerRoom()
    {
        var vm = Room();
        vm.AddRoom("#other");

        vm.SetHistoryCursor("#main", "cursor-main");
        vm.SetHistoryCursor("#other", null);

        Assert.Equal("cursor-main", vm.HistoryCursor("#main"));
        Assert.Null(vm.HistoryCursor("#other"));

        // Visibility follows the active room, not whichever cursor was set last.
        vm.SwitchTo("#other");
        Assert.Equal("loadmore hidden", vm.Model.LoadOlderClass);
        vm.SwitchTo("#main");
        Assert.Equal("loadmore", vm.Model.LoadOlderClass);
    }
}
