using Banter.App;
using CupriFace;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The controls a phone depends on actually answer a finger.
///
/// <para>Everything else measured here is geometry: a control is big enough, a column is not zero
/// wide. None of it says the thing DOES anything when tapped, and a 44dp button wired to nothing
/// passes every one of those checks. <c>TouchDriver</c> (CupriFace 0.25.0) is what closes that —
/// before it, driving a gesture meant inventing a monotonic clock, knowing the slop radius, and
/// knowing that activation lands on finger-UP, each easy enough to get wrong that a gesture built
/// by hand does nothing and reads as a bug in whatever was under test.</para>
/// </summary>
public sealed class TouchGestureTests(ITestOutputHelper output)
{
    private const int PhoneW = 412;
    private const int PhoneH = 915;

    private sealed record Phone(BanterChatApp App, ChatViewModel Vm, CupriDocument Doc, TouchDriver Touch)
    {
        /// <summary>
        /// One host frame. Several handlers do not mutate the model directly — they
        /// <c>ViewModel.Post</c> the change, and posted mutations are drained by
        /// <c>ApplyPending</c> inside <c>Present</c>, on the render thread, immediately before the
        /// frame that will show them. A test that taps and then reads the model without this sees
        /// the state from before the tap and concludes the control is dead.
        /// </summary>
        public void Settle()
        {
            App.Present(PhoneW, PhoneH);
            var p = BanterChatApp.Presentation(PhoneW, PhoneH);
            Doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
        }
    }

    /// <param name="joinRoom">What the head does when a room is joined from the browse rows. The
    /// default does nothing, which is every other test here; the one that cares passes a stand-in
    /// for a session and adds the room to the model, because that is what a join really does and
    /// what anything switching to it depends on.</param>
    private static Phone OnAPhone(
        Func<ChatViewModel, string, Task>? joinRoom = null, Action? roomsListed = null)
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.AddRoom("#other");
        vm.SwitchTo("#main");
        vm.Connected("tcp://host:7770", "alice");

        // One room worth joining, so the browse rows exist: picking one of those has to put the
        // list away exactly as picking a joined room does.
        vm.SetRoomListing([("#main", null, 2), ("#other", null, 1), ("#notes", null, 3)]);
        for (var i = 0; i < 40; i++)
        {
            vm.Append("#main", "dagger", $"message {i}", 0);
        }

        var app = new BanterChatApp(vm)
        {
            JoinRoomAsync = room => joinRoom?.Invoke(vm, room) ?? Task.CompletedTask,
            RoomsListAsync = () =>
            {
                roomsListed?.Invoke();
                return Task.CompletedTask;
            },
        };

        var doc = app.CreateDocument();
        doc.InputProfile = InputProfile.Touch;
        doc.Refresh();

        var p = BanterChatApp.Presentation(PhoneW, PhoneH);
        void Frame() => doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
        Frame();

        // onFrame, so the layout between finger events is the one a host would have produced.
        // Without it a gesture is sequenced against whatever the tree looked like when it started.
        return new Phone(app, vm, doc, new TouchDriver(doc, onFrame: Frame));
    }

    private static IEnumerable<(RenderNode Node, float X, float Y)> Walk(
        RenderNode n, float px = 0, float py = 0)
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

    /// <summary>The middle of the first box carrying this class — where a finger would land.</summary>
    private static (float X, float Y) Middle(CupriDocument doc, string cls)
    {
        var hit = Walk(doc.Root).First(n =>
            n.Node.Element?.GetAttribute("class")?.Split(' ').Contains(cls) == true
            && n.Node.Width > 0 && n.Node.Height > 0);
        return (hit.X + (hit.Node.Width / 2), hit.Y + (hit.Node.Height / 2));
    }

    private static float WidthOf(CupriDocument doc, string cls) =>
        Walk(doc.Root)
            .Where(n => n.Node.Element?.GetAttribute("class")?.Split(' ').Contains(cls) == true)
            .Select(n => n.Node.Width)
            .FirstOrDefault();

    [Fact]
    public void TappingTheMarkOpensTheRoomsAndTappingItAgainPutsThemAway()
    {
        // The single control the whole of mobile navigation hangs off: below the breakpoint the
        // room list and the roster are only reachable through it. Its SIZE has been asserted since
        // touch targets went in; that it answers a finger at all has not.
        var phone = OnAPhone();
        var (x, y) = Middle(phone.Doc, "logo");
        output.WriteLine($"the mark is at ({x:F0},{y:F0})");

        Assert.Equal(0f, WidthOf(phone.Doc, "sidebar"), 1);

        phone.Touch.Tap(x, y);
        Assert.True(WidthOf(phone.Doc, "sidebar") > 0f, "a tap on the mark did not open the rooms");
        Assert.True(WidthOf(phone.Doc, "roster") > 0f, "the people did not come with them");

        // Far enough apart not to be a double tap, which is a different gesture entirely.
        phone.Touch.Advance(1.0);
        phone.Touch.Tap(x, y);
        Assert.Equal(0f, WidthOf(phone.Doc, "sidebar"), 1);
    }

    [Fact]
    public void APressThatIsTakenAwayNeverActivates()
    {
        // Activation lands on finger-UP, so a press the platform reclaims — an ancestor stealing
        // the pointer, the window going away — must leave nothing behind. Worth its own test for
        // any control that acts on release, which on this screen is all of them.
        var phone = OnAPhone();
        var (x, y) = Middle(phone.Doc, "logo");

        phone.Touch.Down(x, y);
        phone.Touch.Cancel();

        Assert.Equal(0f, WidthOf(phone.Doc, "sidebar"), 1);
    }

    [Fact]
    public void TappingARoomInTheOverlaySwitchesToIt()
    {
        // The second half of the navigation: opening the list is no use if the rooms in it do not
        // answer. The tab is 44 tall and inside an overlay that is positioned absolutely over the
        // chat — which is exactly the arrangement where a hit-test can land on what is underneath.
        var phone = OnAPhone();
        var (markX, markY) = Middle(phone.Doc, "logo");
        phone.Touch.Tap(markX, markY);

        Assert.Equal("#main", phone.Vm.Model.ActiveRoom);

        var rooms = Walk(phone.Doc.Root)
            .Where(n => n.Node.Element?.GetAttribute("data-room") is { Length: > 0 })
            .Where(n => n.Node.Width > 0 && n.Node.Height > 0)
            .ToList();
        output.WriteLine(string.Join(", ", rooms.Select(r => r.Node.Element!.GetAttribute("data-room"))));

        var other = rooms.First(r => r.Node.Element!.GetAttribute("data-room") == "#other");
        phone.Touch.Tap(other.X + (other.Node.Width / 2), other.Y + (other.Node.Height / 2));
        phone.Settle();

        Assert.Equal("#other", phone.Vm.Model.ActiveRoom);
    }

    /// <summary>The middle of the first box carrying this attribute with this value.</summary>
    private static (float X, float Y) MiddleOf(CupriDocument doc, string attribute, string value)
    {
        var hit = Walk(doc.Root).First(n =>
            n.Node.Element?.GetAttribute(attribute) == value
            && n.Node.Width > 0 && n.Node.Height > 0);
        return (hit.X + (hit.Node.Width / 2), hit.Y + (hit.Node.Height / 2));
    }

    /// <summary>
    /// Picking a room puts the list away. On this screen the list is an overlay ON the chat, so
    /// leaving it up means the room just opened is underneath it - and there was no scrim and no
    /// tap-outside either, which made every switch three taps with the screen obscured in the
    /// middle of them.
    ///
    /// <para>The test that covered this asserted ActiveRoom had changed. It had. That was never
    /// the complaint.</para>
    /// </summary>
    [Fact]
    public void PickingARoomPutsTheListAway()
    {
        var phone = OnAPhone();
        var (markX, markY) = Middle(phone.Doc, "logo");
        phone.Touch.Tap(markX, markY);
        Assert.True(WidthOf(phone.Doc, "sidebar") > 0f, "the list did not open");

        var (roomX, roomY) = MiddleOf(phone.Doc, "data-room", "#other");
        phone.Touch.Tap(roomX, roomY);
        phone.Settle();

        output.WriteLine($"sidebar {WidthOf(phone.Doc, "sidebar"):F0} wide, "
            + $"roster {WidthOf(phone.Doc, "roster"):F0}, timeline {WidthOf(phone.Doc, "timeline"):F0}");

        Assert.Equal("#other", phone.Vm.Model.ActiveRoom);
        Assert.Equal(0f, WidthOf(phone.Doc, "sidebar"), 1);
        Assert.Equal(0f, WidthOf(phone.Doc, "roster"), 1);

        // …and the room you asked for is what is on screen, which is the whole point of closing it.
        Assert.True(WidthOf(phone.Doc, "timeline") > 0f, "the timeline is not on screen");
    }

    /// <summary>
    /// The same for a room being JOINED from the browse rows. It is a different handler, and a fix
    /// applied to one of the two is the kind that looks complete until somebody taps the other.
    ///
    /// <para>And joining goes TO the room. Joining never switched - that is right for the joins a
    /// head makes at startup, looping over remembered rooms - but somebody who taps a room has
    /// said where they want to be. On an emulator this was a dead end: the list closed and nothing
    /// had changed, so the only way in was to open the list and tap the room a second time.</para>
    /// </summary>
    [Fact]
    public async Task JoiningARoomPutsTheListAwayAndGoesThere()
    {
        var joined = new List<string>();
        var phone = OnAPhone((vm, room) =>
        {
            joined.Add(room);

            // What a session does: the room exists in the model after a join, which is what
            // anything switching to it depends on.
            vm.AddRoom(room);
            return Task.CompletedTask;
        });

        var (markX, markY) = Middle(phone.Doc, "logo");
        phone.Touch.Tap(markX, markY);

        var (joinX, joinY) = MiddleOf(phone.Doc, "data-join", "#notes");
        phone.Touch.Tap(joinX, joinY);

        // The join is awaited before the switch, so one turn of the loop is not enough.
        await Task.Delay(50);
        phone.Settle();

        Assert.Equal(["#notes"], joined);
        Assert.Equal("#notes", phone.Vm.Model.ActiveRoom);
        Assert.Equal(0f, WidthOf(phone.Doc, "sidebar"), 1);
        Assert.True(WidthOf(phone.Doc, "timeline") > 0f, "the timeline is not on screen");
    }

    /// <summary>
    /// The room name in the header opens the list. It is where a thumb goes and where every other
    /// chat app puts this control; before, it was the one thing on screen that named the room and
    /// the one thing that did nothing when tapped, while the only control that worked was an
    /// unlabelled mark sitting where apps put a logo.
    ///
    /// <para>It only ever OPENS, and that is geometry rather than a decision: <c>.sidebar.open</c>
    /// is fixed at left 56 and 232 wide, so the open list covers the header it was opened from.
    /// The rail is at 56px, which is the whole reason the mark stays reachable and remains the way
    /// to put the list away without picking anything. The second half of this test asserted that
    /// tapping the name again closed it, which could not work and did not - and passed in a run
    /// whose output I had truncated.</para>
    /// </summary>
    [Fact]
    public void TappingTheRoomNameOpensTheRooms()
    {
        var phone = OnAPhone();
        Assert.Equal(0f, WidthOf(phone.Doc, "sidebar"), 1);

        var (x, y) = Middle(phone.Doc, "header-title");
        output.WriteLine($"the room name is at ({x:F0},{y:F0})");
        phone.Touch.Tap(x, y);

        Assert.True(WidthOf(phone.Doc, "sidebar") > 0f, "tapping the room name did not open the rooms");

        // The list is now over that very point, so this is what a second tap would land on. Said
        // as a measurement rather than left to the prose above, because it is the reason the mark
        // is still the way out.
        var under = Walk(phone.Doc.Root)
            .Where(n => n.Node.Width > 0 && n.Node.Height > 0)
            .Where(n => x >= n.X && x <= n.X + n.Node.Width && y >= n.Y && y <= n.Y + n.Node.Height)
            .Select(n => n.Node.Element?.GetAttribute("class"))
            .OfType<string>()
            .ToList();
        output.WriteLine($"over the room name now: {string.Join(" / ", under)}");
        Assert.Contains(under, c => c.Contains("sidebar", StringComparison.Ordinal));

        // The mark is left of it - the rail is 56 wide and the list starts there - so that is what
        // closes it.
        var (markX, markY) = Middle(phone.Doc, "logo");
        Assert.True(markX < 56f, $"the mark is at {markX:F0}, which the open list would cover");
        phone.Touch.Advance(1.0);
        phone.Touch.Tap(markX, markY);
        Assert.Equal(0f, WidthOf(phone.Doc, "sidebar"), 1);
    }

    /// <summary>
    /// On a wide window the room name is just a room name. The list is a column already on screen
    /// there, so a title that put it away would be a surprise on a target everybody clicks - and
    /// the caret that says "this opens something" is not drawn there either.
    /// </summary>
    [Fact]
    public void OnADesktopTheRoomNameIsNotAControl()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.Connected("tcp://host:7770", "alice");

        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.Refresh();
        var p = BanterChatApp.Presentation(1280, 800);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var before = WidthOf(doc, "sidebar");
        Assert.True(before > 0f, "the room list should be a column at this width");

        var hit = Walk(doc.Root).First(n =>
            n.Node.Element?.GetAttribute("class")?.Split(' ').Contains("header-title") == true
            && n.Node.Width > 0);
        doc.DispatchClick(hit.X + (hit.Node.Width / 2), hit.Y + (hit.Node.Height / 2));
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        output.WriteLine($"sidebar was {before:F0}, now {WidthOf(doc, "sidebar"):F0}");
        Assert.Equal(before, WidthOf(doc, "sidebar"), 1);

        // The caret is a promise, so it is not made where the control does nothing.
        var caret = Walk(doc.Root)
            .Where(n => n.Node.Element?.GetAttribute("class")?.Split(' ').Contains("room-caret") == true)
            .Select(n => n.Node.Width)
            .FirstOrDefault();
        Assert.Equal(0f, caret, 1);
    }

    /// <summary>
    /// Opening the list asks the server what is in it. It was read at join and never again, so a
    /// room another client made after you signed in was simply absent - no error and no empty
    /// state, just a list that looked complete and was not. Found on an emulator, where a second
    /// client created #notes and the phone could not see it until the app was restarted.
    ///
    /// <para>Closing asks nothing. Putting the list away is not a reason to read it, and on a
    /// phone that would be a round trip for every dismissal.</para>
    /// </summary>
    [Fact]
    public void OpeningTheRoomListReadsItAgain()
    {
        var reads = 0;
        var phone = OnAPhone(roomsListed: () => reads++);
        var (x, y) = Middle(phone.Doc, "logo");

        phone.Touch.Tap(x, y);
        Assert.True(WidthOf(phone.Doc, "sidebar") > 0f, "the list did not open");
        Assert.Equal(1, reads);

        // …and away again, which asks nothing.
        phone.Touch.Advance(1.0);
        phone.Touch.Tap(x, y);
        Assert.Equal(0f, WidthOf(phone.Doc, "sidebar"), 1);
        Assert.Equal(1, reads);

        // Opened from the room name too, since that is the other way in.
        phone.Touch.Advance(1.0);
        var (nameX, nameY) = Middle(phone.Doc, "header-title");
        phone.Touch.Tap(nameX, nameY);
        output.WriteLine($"{reads} reads after open, close, open");
        Assert.Equal(2, reads);
    }
}
