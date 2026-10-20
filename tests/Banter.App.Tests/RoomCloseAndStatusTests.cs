using Banter.App;
using CupriFace.Dom;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// Closing a room from its tab, and who is running a room shown as a status pill rather than as
/// lines in the timeline.
/// </summary>
public sealed class RoomCloseAndStatusTests
{
    private static IEnumerable<(RenderNode Node, float X, float Y)> Walk(RenderNode n, float px = 0, float py = 0)
    {
        var x = px + n.X;
        var y = py + n.Y;
        yield return (n, x, y);
        foreach (var c in n.Children)
        {
            foreach (var d in Walk(c, x, y))
            {
                yield return d;
            }
        }
    }

    private static ChatViewModel TwoRooms(bool admin)
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.SetIsAdmin(admin);
        vm.AddRoom("#main");
        vm.AddRoom("#other");
        vm.SwitchTo("#main");
        vm.Connected("tcp://host:7770", "alice");
        return vm;
    }

    [Fact]
    public void OnlyAnAdminIsOfferedTheClose()
    {
        Assert.All(TwoRooms(admin: true).Model.Rooms, r => Assert.Equal("tab-close", r.CloseClass));
        Assert.All(TwoRooms(admin: false).Model.Rooms, r => Assert.Equal("no-close", r.CloseClass));

        // Rooms that were already on the rail when the account turned out to be an admin.
        var late = TwoRooms(admin: false);
        late.SetIsAdmin(true);
        Assert.All(late.Model.Rooms, r => Assert.Equal("tab-close", r.CloseClass));
    }

    /// <summary>
    /// Reported from a phone: "Close #room?" appeared behind the room drawer it was asked from.
    /// The drawer is fixed and the engine paints fixed elements above everything else whatever
    /// its z-index, so an absolute confirm with a higher z-index still lost. Two things now: the
    /// drawer goes when the question is asked, and the question is on top even if it is not.
    /// </summary>
    [Fact]
    public void TheQuestionIsOverTheRoomDrawerOnAPhone()
    {
        var vm = TwoRooms(admin: true);
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.InputProfile = CupriFace.Interaction.InputProfile.Touch;

        vm.ToggleRooms(narrow: true);
        Assert.True(vm.RoomsShowing);
        vm.ConfirmCloseRoom("#other");
        Assert.False(vm.RoomsShowing);

        // And with the drawer put back up, the question still wins the tap.
        vm.ToggleRooms(narrow: true);
        doc.Refresh();
        var p = BanterChatApp.Presentation(412, 915);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var card = Walk(doc.Root).First(n =>
            n.Node.Element?.GetAttribute("class")?.Split(' ').Contains("confirm-card") == true && n.Node.Width > 0).Node;
        var box = CupriFace.Interaction.HitTesting.AbsoluteBox(card);
        var hit = doc.HitTest(box.X + (box.W / 2), box.Y + (box.H / 2));
        Assert.NotNull(hit?.Element?.Closest(".confirm-card"));
    }

    [Fact]
    public void ClickingTheCrossAsksAndDoesNotSwitchRooms()
    {
        var commands = new List<string>();
        var vm = TwoRooms(admin: true);
        var app = new BanterChatApp(vm)
        {
            CommandAsync = (room, text) =>
            {
                commands.Add($"{room} {text}");
                return Task.CompletedTask;
            },
        };

        using var doc = app.CreateDocument();
        doc.Refresh();
        var p = BanterChatApp.Presentation(1280, 800);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var cross = Walk(doc.Root).First(n =>
            n.Node.Element?.GetAttribute("data-room-close") == "#other" && n.Node.Width > 0);
        doc.DispatchClick(cross.X + (cross.Node.Width / 2), cross.Y + (cross.Node.Height / 2));
        app.Present(1280, 800);

        Assert.True(vm.ConfirmOpen, "closing a room did not ask first");
        Assert.Equal("Close #other?", vm.Model.ConfirmTitle);
        Assert.Equal("#main", vm.Model.ActiveRoom);
        Assert.Empty(commands);

        // Confirmed, it goes to the session as the command a person could have typed.
        var confirmed = vm.TakeConfirmed();
        Assert.Equal(new Confirmed(ConfirmAct.CloseRoom, "#other", ""), confirmed);
    }

    [Theory]
    [InlineData("local is now the delegator for this room (local agent, preferred for delegation).")]
    [InlineData("No delegator: no agents in the room. Agents answer when mentioned.")]
    [InlineData("Room is now delegated: one agent routes requests.")]
    [InlineData("Room is now mention mode: agents answer when named.")]
    [InlineData("Interregnum: local, the local delegator, left. The seat is held for a local agent until one takes it, or an admin ends this.")]
    [InlineData("Republic restored: local took the seat.")]
    public void TheServersDispatchLinesAreLeftToThePill(string text) =>
        Assert.True(ChatViewModel.IsDispatchNotice("banter", text));

    [Fact]
    public void NothingAPersonSaysIsTakenForADispatchLine()
    {
        Assert.False(ChatViewModel.IsDispatchNotice("alice", "local is now the delegator for this room (as you see)."));
        Assert.False(ChatViewModel.IsDispatchNotice("banter", "task 1234 lease expired and was released"));
    }

    [Fact]
    public void ThePillSaysWhetherAnybodyIsRunningTheRoom()
    {
        var vm = new ChatViewModel();
        Assert.Equal("dispatch hidden", vm.Model.DispatchClass);

        vm.AddRoom("#main");
        Assert.Equal("dispatch off", vm.Model.DispatchClass);

        vm.SetDelegator("#main", "local");
        vm.SetDispatchMode("#main", "delegated");
        Assert.Equal("dispatch on", vm.Model.DispatchClass);
        Assert.Equal("delegated · local", vm.Model.Dispatch);

        vm.SetDelegator("#main", null);
        Assert.Equal("dispatch off", vm.Model.DispatchClass);
    }

    [Fact]
    public void TheRegimeIsOneLetterBesideTheDelegator()
    {
        var vm = TwoRooms(admin: false);
        vm.SetDelegator("#main", "local");
        Assert.Equal("R", vm.Model.RegimeMark);
        Assert.Equal("regime republic", vm.Model.RegimeClass);

        vm.SetRegime("#main", interregnum: true);
        Assert.Equal("I", vm.Model.RegimeMark);
        Assert.Equal("regime interregnum", vm.Model.RegimeClass);
        Assert.StartsWith("Interregnum", vm.Model.RegimeTip);

        // Per room: the other room is still a republic, and switching shows it.
        vm.SwitchTo("#other");
        Assert.Equal("R", vm.Model.RegimeMark);
        vm.SwitchTo("#main");
        Assert.Equal("I", vm.Model.RegimeMark);
    }

    [Fact]
    public void AnAdminEndsAnInterregnumFromTheLetterAfterSayingWhatThatMeans()
    {
        var commands = new List<string>();
        var vm = TwoRooms(admin: true);
        vm.SetDelegator("#main", null);
        vm.SetRegime("#main", interregnum: true);
        Assert.Equal("regime interregnum act", vm.Model.RegimeClass);
        Assert.EndsWith("Click to end it.", vm.Model.RegimeTip);

        var app = new BanterChatApp(vm)
        {
            CommandAsync = (room, text) =>
            {
                commands.Add($"{room} {text}");
                return Task.CompletedTask;
            },
        };

        using var doc = app.CreateDocument();
        doc.Refresh();
        var p = BanterChatApp.Presentation(1280, 800);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var letter = Walk(doc.Root).First(n =>
            n.Node.Element?.GetAttribute("data-regime") == "1" && n.Node.Width > 0);
        doc.DispatchClick(letter.X + (letter.Node.Width / 2), letter.Y + (letter.Node.Height / 2));

        Assert.True(vm.ConfirmOpen, "ending an interregnum did not ask first");
        Assert.Equal("End the interregnum in #main?", vm.Model.ConfirmTitle);
        Assert.Equal(new Confirmed(ConfirmAct.EndInterregnum, "#main", ""), vm.TakeConfirmed());
    }

    [Fact]
    public void AMemberIsNotOfferedTheEnd()
    {
        var vm = TwoRooms(admin: false);
        vm.SetDelegator("#main", null);
        vm.SetRegime("#main", interregnum: true);

        vm.ConfirmEndInterregnum("#main");

        Assert.False(vm.ConfirmOpen);
        Assert.DoesNotContain("Click", vm.Model.RegimeTip);
    }
}
