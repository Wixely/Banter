using Banter.App;
using SkiaSharp;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The room list: joined rooms with their parentage, and the rest offered for joining. Sub-rooms
/// and admin oversight both put people into rooms they did not ask for, so the list has to make
/// sense of that rather than showing a flat set of unrelated names.
/// </summary>
public sealed class RoomListingTests
{
    private static ChatViewModel Joined(params string[] rooms)
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        foreach (var room in rooms)
        {
            vm.AddRoom(room);
        }

        return vm;
    }

    [Fact]
    public void ASubRoomIsShownIndentedUnderItsParent()
    {
        var vm = Joined("#main", "#fix-parser-9c55");

        vm.SetRoomListing([("#main", null, 3), ("#fix-parser-9c55", "#main", 2)]);

        Assert.Equal("#main", vm.Model.Rooms.Single(r => r.Name == "#main").Label);
        Assert.Contains("└", vm.Model.Rooms.Single(r => r.Name == "#fix-parser-9c55").Label);
    }

    [Fact]
    public void RoomsYouAreNotInAreOfferedForJoining()
    {
        var vm = Joined("#main");

        vm.SetRoomListing([("#main", null, 3), ("#other", null, 1)]);

        var offered = Assert.Single(vm.Model.Browse);
        Assert.Equal("#other", offered.Name);
        Assert.Equal("1 member", offered.Members);
        Assert.Equal("browse", vm.Model.BrowseClass);
    }

    [Fact]
    public void TheBrowseListIsHiddenWhenYouAreInEverything()
    {
        var vm = Joined("#main");

        vm.SetRoomListing([("#main", null, 2)]);

        Assert.Empty(vm.Model.Browse);
        Assert.Equal("browse hidden", vm.Model.BrowseClass);
    }

    [Fact]
    public void AJoinedRoomIsNeverAlsoOfferedForJoining()
    {
        var vm = Joined("#main", "#other");

        vm.SetRoomListing([("#main", null, 2), ("#other", null, 2)]);

        Assert.Empty(vm.Model.Browse);
    }

    [Fact]
    public void ARoomYouWerePutIntoAppearsAsJoinedNotAsAnOffer()
    {
        // An agent opening a sub-room, or the admin oversight rule, puts you into a room without
        // you asking. It must read as somewhere you are, not somewhere you could go.
        var vm = Joined("#main");
        vm.AddRoom("#thompson-matter-ccff");

        vm.SetRoomListing([("#main", null, 2), ("#thompson-matter-ccff", "#main", 3)]);

        Assert.Empty(vm.Model.Browse);
        Assert.Contains("└", vm.Model.Rooms.Single(r => r.Name == "#thompson-matter-ccff").Label);
    }

    [Fact]
    public void MemberCountsReadNaturally()
    {
        var vm = Joined("#main");

        vm.SetRoomListing([("#main", null, 1), ("#one", null, 1), ("#many", null, 4)]);

        Assert.Equal("1 member", vm.Model.Browse.Single(b => b.Name == "#one").Members);
        Assert.Equal("4 members", vm.Model.Browse.Single(b => b.Name == "#many").Members);
    }

    [Fact]
    public void TheAppRendersWithABrowseListPresent()
    {
        var vm = Joined("#main");
        vm.SetRoomListing([("#main", null, 2), ("#other", null, 1), ("#child", "#main", 2)]);
        vm.Append("#main", "alice", "hello", 0);

        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);
        var pixels = doc.RenderToPixels(1100, 760, SKColors.Black);

        Assert.Contains(pixels, b => b != 0);
    }
}
