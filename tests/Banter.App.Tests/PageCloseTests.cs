using Banter.App;
using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Every page's way out is in the top-right corner of the page, at every size. It used to sit in
/// the detail pane's head - the right of a wide card, but the middle of a narrow one, where the
/// list stacks above the pane.
/// </summary>
public sealed class PageCloseTests(ITestOutputHelper output)
{
    public static TheoryData<string, int, int> Pages
    {
        get
        {
            var data = new TheoryData<string, int, int>();
            foreach (var page in new[] { "tools", "agents", "users", "work", "hubs", "settings" })
            {
                foreach (var (w, h) in new[] { (1280, 800), (412, 915), (915, 412) })
                {
                    data.Add(page, w, h);
                }
            }

            return data;
        }
    }

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

    [Theory]
    [MemberData(nameof(Pages))]
    public void TheCloseIsInTheTopRightCornerOfThePage(string page, int w, int h)
    {
        var app = AppPages.Showing(page);
        using var doc = app.CreateDocument();
        var p = BanterChatApp.Presentation(w, h);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var close = Walk(doc.Root).Single(n =>
            n.Node.Element?.GetAttribute("class")?.Split(' ').Contains("page-close") == true
            && n.Node.Width > 0);

        // The card it closes: the nearest ancestor that is a page's card.
        var card = Walk(doc.Root).Single(n =>
            n.Node.Element?.GetAttribute("class")?.Split(' ') is { } c
            && (c.Contains("mgmt-card") || c.Contains("toolpanel-inner"))
            && n.Node.Width > 0);

        var right = card.X + card.Node.Width - (close.X + close.Node.Width);
        var top = close.Y - card.Y;
        output.WriteLine($"{page} at {w}x{h}: {right:F0} from the right, {top:F0} from the top of a {card.Node.Height:F0} card");

        Assert.InRange(right, 0f, 24f);
        Assert.InRange(top, 0f, 24f);
    }
}
