using Banter.App;
using CupriFace;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// You can always reach the Connect button.
///
/// <para>The connect screen is the one page that must work before anything else does, and it is
/// the only page with no way out: there is no rail, no menu and nothing to scroll to if the card
/// does not fit. A viewport shorter than the card costs you the button, the status line that says
/// why the last attempt failed, and the hint under it.</para>
///
/// <para>This is the settings card's scar in a second place (<see cref="SettingsReachabilityTests"/>):
/// <c>.connect</c> is <c>height: 100%</c> with a card taller than that inside it, and a box with a
/// hard ceiling and no scrolling does not clip - it paints over whatever follows and the overflow
/// is simply unreachable. CupriDoctor called it CF0070 on a landscape phone.</para>
/// </summary>
public sealed class ConnectReachabilityTests(ITestOutputHelper output)
{
    private static RenderNode? Find(RenderNode node, string cls)
    {
        if (node.Element?.GetAttribute("class") is { } c
            && c.Split(' ').Contains(cls, StringComparer.Ordinal))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (Find(child, cls) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>A landscape phone is the case that was actually broken: 412 logical pixels of
    /// height for a card that needs 540. The short desktop windows are the same failure reached a
    /// different way, and the portrait phone is the control - it has room, and must stay simple.
    /// </summary>
    [Theory]
    [InlineData(915, 412)]        // landscape phone: what CF0070 reported
    [InlineData(800, 412)]        // shorter still
    [InlineData(1280, 520)]       // the letterbox desktop window
    [InlineData(412, 915)]        // portrait phone: fits, and should need no scrolling
    [InlineData(1280, 1050)]      // the ordinary desktop window
    public void TheWholeCardCanBeReached(int w, int h)
    {
        var app = AppPages.Showing("connect");
        using var doc = app.CreateDocument();
        doc.InputProfile = InputProfile.Touch;
        doc.Refresh();

        var p = BanterChatApp.Presentation(w, h);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var pane = Find(doc.Root, "connect");
        Assert.NotNull(pane);
        var card = HitTesting.AbsoluteBox(Find(doc.Root, "connect-card")!);
        output.WriteLine(
            $"{w}x{h} (logical {p.LogicalWidth:F0}x{p.LogicalHeight:F0}): pane {pane.Height:F0} "
            + $"content={pane.ScrollContentHeight:F0} scrollable={pane.IsScrollable}; "
            + $"card y={card.Y:F0} h={card.H:F0}");

        // Still centred. The pane became a flex COLUMN to make the overflow scrollable, which
        // moves horizontal centring from justify-content to align-items - a silent left-align is
        // exactly the kind of thing that change breaks, and this page is the first screen anybody
        // sees.
        var slack = p.LogicalWidth - card.W;
        Assert.True(
            Math.Abs(card.X - (slack / 2)) <= 1f,
            $"the card sits at x={card.X:F0} where centred is {slack / 2:F0}");

        // Either the card fits in the viewport, or the pane holding it scrolls. Anything else and
        // the bottom of the card - the button, the status, the hint - is off-screen for good.
        if (card.Y + card.H <= p.LogicalHeight + 0.5f)
        {
            return;
        }

        Assert.True(
            pane.IsScrollable,
            $"the card ends {card.Y + card.H - p.LogicalHeight:F0}px past the window and nothing scrolls");

        // And "scrollable" has to mean a gesture moves it, or it is a number nobody can act on.
        var box = HitTesting.ScreenBox(pane);
        var handled = doc.DispatchWheel(box.X + (box.W / 2), box.Y + (box.H / 2), 120);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        Assert.True(handled, "a wheel on the connect pane should scroll it");
        Assert.True(Find(doc.Root, "connect")!.ScrollY > 0.5f, "and the pane should have moved");
    }
}
