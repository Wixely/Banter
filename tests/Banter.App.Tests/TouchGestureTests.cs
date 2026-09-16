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

    private static Phone OnAPhone()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.AddRoom("#other");
        vm.SwitchTo("#main");
        vm.Connected("tcp://host:7770", "alice");
        for (var i = 0; i < 40; i++)
        {
            vm.Append("#main", "dagger", $"message {i}", 0);
        }

        var app = new BanterChatApp(vm);
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
}
