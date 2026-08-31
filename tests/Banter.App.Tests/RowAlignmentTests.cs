using Banter.App;
using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Things on a line sit on the same line, and a row of them fits the window.
///
/// <para>Both are laid-out facts rather than declared ones, and neither is visible in the markup:
/// an explicit height on a button leaves its label against the top of the box, a border sits
/// outside that height so two buttons declared the same height come out 2px apart, and a flex item
/// defaults to <c>min-width: auto</c> — which kept the composer at a width it had no room for and
/// pushed Send off the right edge of the window as soon as a third button appeared beside it.</para>
/// </summary>
public sealed class RowAlignmentTests(ITestOutputHelper output)
{
    private const int Width = 1100;
    private const int Height = 760;

    /// <summary>Every horizontal row in the window. Column stacks are deliberately absent.</summary>
    private static readonly string[] Rows =
    [
        "composer-row", "composer-hint", "tab", "browse-row", "sidebar-footer", "header",
        "line", "msg-head", "agent-row", "agent-line", "voice-row", "toolpanel-head", "tool-line",
    ];

    private static IEnumerable<(RenderNode Node, float Left, float Top)> Walk(
        RenderNode node, float parentLeft = 0, float parentTop = 0)
    {
        var left = parentLeft + node.X;
        var top = parentTop + node.Y;
        yield return (node, left, top);
        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child, left, top))
            {
                yield return descendant;
            }
        }
    }

    private static ChatViewModel Furnished()
    {
        var vm = new ChatViewModel();
        vm.SetNick("anmcguinness");
        vm.SetStatus("connected", true);
        vm.AddRoom("#main");
        vm.AddRoom("#agents");
        vm.SwitchTo("#main");
        vm.SetTopic("#main", "the room everything lands in");
        vm.SetDispatchMode("#main", "delegated");

        // Every optional control on at once: each one is a row that only exists on some heads, and
        // the crowded case is the one that breaks.
        vm.EnableAttach();
        vm.EnableVoice(readbackAvailable: true);

        vm.Append("#main", "bob", "a message with some text in it", 0, id: "m1");
        vm.Append("#main", "anmcguinness", "mine", 0, id: "m2");
        vm.MarkEdited("#main", "m2", "mine, rewritten");
        vm.Append("#agents", "carol", "unread", 0, id: "m3");
        vm.SetAgents("#main", [("local-a", true, "chat, code", true), ("scout", false, "search", false)]);
        vm.SetTasks("#main", [("t1", "Bump the package", "held", "local-a")]);
        vm.SetRoomListing([("#design", null, 4)]);
        vm.SetToolCatalogue([("read_file", "fs", "Read a file"), ("gh_list_issues", "github", "List issues")]);
        vm.SelectToolAgent("local-a");
        vm.SetToolGrants("local-a", ["read_file"]);
        return vm;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EverythingOnARowSharesItsCentreLine(bool toolPanelOpen)
    {
        var vm = Furnished();
        vm.ShowToolPanel(toolPanelOpen);

        using var doc = new BanterChatApp(vm).CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var offenders = new List<string>();
        var checkedRows = 0;

        foreach (var (node, _, top) in Walk(doc.Root))
        {
            var classes = (node.Element?.GetAttribute("class") ?? "").Split(' ');
            if (node.Height <= 0 || !Rows.Any(classes.Contains))
            {
                continue;
            }

            var centres = node.Children
                .Where(c => c.Height > 0)
                .Select(c => top + c.Y + (c.Height / 2))
                .ToList();
            if (centres.Count < 2)
            {
                continue;
            }

            checkedRows++;
            var spread = centres.Max() - centres.Min();
            if (spread > 1)
            {
                offenders.Add($"{string.Join('.', classes)} spreads its children over {spread:F1}px");
            }
        }

        output.WriteLine($"{checkedRows} rows checked, {offenders.Count} misaligned");
        Assert.True(checkedRows >= 10, $"only {checkedRows} rows were laid out — the window is not furnished");
        Assert.Empty(offenders);
    }

    /// <summary>
    /// Pointing at the composer, or typing in it, must not move anything.
    ///
    /// <para>The component draws a 2px border when hovered or focused, and that border lands
    /// OUTSIDE the box because the engine sizes content-box and does not honour
    /// <c>box-sizing</c> (CupriFace#76). A field declared with no border at rest therefore grew
    /// 4px the instant the pointer crossed it, lifting the composer, its buttons and the hint
    /// under it — so the whole bar twitched as the mouse passed over. The stylesheet holds the
    /// space open with a transparent border and lets only the colour change.</para>
    /// </summary>
    [Fact]
    public void PointingAtTheComposerMovesNothing()
    {
        var vm = Furnished();
        using var doc = new BanterChatApp(vm).CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var (px, py) = PointOnComposer(doc);

        (float X, float Y, float W, float H)[] Geometry()
        {
            doc.BuildDisplayList(Width, Height);
            return Walk(doc.Root)
                .Where(n => n.Node.Element?.TagName == "CUPRI-TEXTAREA"
                            || n.Node.Element?.TagName == "CUPRI-BUTTON"
                            || (n.Node.Element?.GetAttribute("class") ?? "") is "composer-row" or "composer-hint")
                .Select(n => (n.Left, n.Top, n.Node.Width, n.Node.Height))
                .ToArray();
        }

        var resting = Geometry();
        Assert.NotEmpty(resting);

        doc.DispatchPointerMove(px, py);
        var hovered = Geometry();

        doc.DispatchClick(px, py, 1);
        var focused = Geometry();

        output.WriteLine($"resting {resting[0]}, hovered {hovered[0]}, focused {focused[0]}");
        Assert.Equal(resting, hovered);
        Assert.Equal(resting, focused);
    }

    /// <summary>A point that really is inside the composer, found by asking what is painted there.</summary>
    private static (float X, float Y) PointOnComposer(CupriFace.CupriDocument doc)
    {
        for (var y = (float)Height - 1; y > Height - 160; y -= 2)
        {
            for (var x = 40f; x < Width - 40; x += 8)
            {
                if (doc.HitTest(x, y)?.Element?.Closest("cupri-textarea") is not null)
                {
                    return (x, y);
                }
            }
        }

        throw new Xunit.Sdk.XunitException("nothing painted belongs to the composer");
    }

    [Fact]
    public void TheComposerFitsTheWindowWithEveryButtonShowing()
    {
        var vm = Furnished();

        using var doc = new BanterChatApp(vm).CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var nodes = Walk(doc.Root).ToList();
        var row = nodes.Single(n => (n.Node.Element?.GetAttribute("class") ?? "") == "composer-row");
        var rowRight = row.Left + row.Node.Width;

        // Talk, Attach and Send are all present here. Measured before the fix: the last button
        // ended 92px past this edge, entirely outside the window.
        foreach (var (node, left, _) in Walk(row.Node, row.Left - row.Node.X, row.Top - row.Node.Y))
        {
            if (node.Element?.TagName != "CUPRI-BUTTON")
            {
                continue;
            }

            var label = node.Element.GetAttribute("class") ?? "";
            output.WriteLine($"{label}: {left:F1}..{left + node.Width:F1} (row ends {rowRight:F1})");
            Assert.True(
                left + node.Width <= rowRight + 0.5f,
                $"the {label} button ends at {left + node.Width:F1}, past the composer's own {rowRight:F1}");
        }
    }
}
