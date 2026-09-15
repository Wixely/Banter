using Banter.App;
using CupriFace;
using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The window decides the layout, rather than the layout assuming a window.
///
/// <para>Everything here is measured through <see cref="CupriDocument.BuildFrame"/> rather than
/// <c>BuildDisplayList</c>. The difference is the whole subject: <c>BuildDisplayList</c> lays out
/// at the size it is handed and returns, so it divides by no zoom and re-resolves no
/// <c>@media</c> — a responsive fact cannot be observed through it at all.</para>
/// </summary>
public sealed class ResponsiveLayoutTests(ITestOutputHelper output)
{
    private static ChatViewModel Furnished()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.Connected("tcp://host:7770", "alice");
        for (var i = 0; i < 40; i++)
        {
            vm.Append("#main", i % 2 == 0 ? "dagger" : "alice", $"message {i}", 0);
        }

        return vm;
    }

    private static IEnumerable<(RenderNode Node, float Top)> Walk(RenderNode node, float parentTop = 0)
    {
        var top = parentTop + node.Y;
        yield return (node, top);
        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child, top))
            {
                yield return descendant;
            }
        }
    }

    private static (RenderNode Node, float Top) Find(CupriDocument doc, string cls) =>
        Walk(doc.Root).First(n =>
            n.Node.Element?.GetAttribute("class")?.Split(' ').Contains(cls) == true);

    private static CupriDocument Laid(ChatViewModel vm, float width, float height)
    {
        var app = new BanterChatApp(vm);
        var doc = app.CreateDocument();
        var p = BanterChatApp.Presentation(width, height);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
        return doc;
    }

    [Theory]
    // Two windows of the same width and very different heights. The scrollback is the only thing
    // in the column that can absorb the difference, so all of it should land there.
    [InlineData(1280, 800)]
    [InlineData(1280, 1200)]
    public void TheScrollbackFillsTheColumnItIsIn(int w, int h)
    {
        var vm = Furnished();
        using var doc = Laid(vm, w, h);

        var (timeline, top) = Find(doc, "timeline");
        output.WriteLine($"{w}x{h} -> timeline {timeline.Height:F0}px at y={top:F0}, viewport {doc.ViewportHeight:F0}");

        // It used to be height="620" — an INLINE height the component wrote, which beat the
        // `.timeline { flex: 1 }` rule that was already there. So the scrollback was 620px in
        // every window, and in a tall one the composer floated well clear of the bottom.
        Assert.True(timeline.Height > 620f,
            $"the scrollback is {timeline.Height:F0}px — still its old fixed height, not the column's");

        // Everything below it (composer, hint, voice row) is a fixed cost, so what is left over
        // after the scrollback should not grow with the window.
        var below = doc.ViewportHeight - (top + timeline.Height);
        Assert.True(below < 200f, $"{below:F0}px is unaccounted for below the scrollback");
    }

    [Fact]
    public void TheScrollbackTakesTheWholeOfAnExtraHundredPixels()
    {
        var vm = Furnished();
        using var shortWindow = Laid(vm, 1280, 800);
        using var tallWindow = Laid(vm, 1280, 900);

        var grew = Find(tallWindow, "timeline").Node.Height - Find(shortWindow, "timeline").Node.Height;
        output.WriteLine($"100px of window gave the scrollback {grew:F0}px");

        // Nothing else in the column is allowed to grow, so the scrollback should take all 100.
        Assert.Equal(100f, grew, 1);
    }

    [Fact]
    public void APhoneIsLaidOutAtItsOwnWidth()
    {
        // The point of Adaptive, stated as the thing the rest of the responsive work depends on:
        // the cascade has to be evaluated against 412, or no breakpoint written below the design
        // width can ever match.
        var vm = Furnished();
        using var doc = Laid(vm, 412, 915);

        output.WriteLine($"viewport {doc.ViewportWidth:F0}x{doc.ViewportHeight:F0}");

        Assert.Equal(412f, doc.ViewportWidth, 1);
        Assert.Equal(915f, doc.ViewportHeight, 1);
    }

    [Theory]
    [InlineData(412, 915)]
    [InlineData(915, 412)]
    [InlineData(640, 800)]
    public void OnANarrowScreenTheChatGetsTheWindow(int w, int h)
    {
        var vm = Furnished();
        using var doc = Laid(vm, w, h);

        var main = Find(doc, "main").Node;
        var rail = Find(doc, "rail").Node;
        output.WriteLine($"{w}x{h} -> rail {rail.Width:F0}, main {main.Width:F0}");

        // It used to be 0. The three columns of chrome shrank rather than overflowing, in
        // proportion, and the chat was what the proportion left over — which was nothing.
        Assert.True(main.Width >= doc.ViewportWidth - rail.Width - 1f,
            $"the chat got {main.Width:F0} of {doc.ViewportWidth:F0}, with only the rail beside it");

        // And the two columns that are references rather than the conversation are gone.
        Assert.DoesNotContain(Walk(doc.Root), n =>
            n.Node.Element?.GetAttribute("class")?.Split(' ').Contains("roster") == true
            && n.Node.Width > 0);
    }

    [Fact]
    public void AWideWindowStillGetsAllFourColumns()
    {
        // The control. Everything above is about one shape of window, and none of it is allowed to
        // reach the shape the app was designed for.
        var vm = Furnished();
        using var doc = Laid(vm, 1280, 800);

        // 249, not the declared 248: `.sidebar` carries `border-right: 1px`, and since CupriFace
        // 0.25.0 a per-side border is parsed and therefore OCCUPIES WIDTH. It was thrown away
        // before, so eleven declarations in this app drew nothing and cost nothing; now they draw
        // and cost a pixel apiece. Kept exact rather than loosened — this number moving again is
        // worth being told about.
        Assert.Equal(249f, Find(doc, "sidebar").Node.Width, 1);
        Assert.True(Find(doc, "roster").Node.Width > 200f, "the roster lost its column");
        Assert.True(Find(doc, "main").Node.Width > 600f, "the chat lost its width");
    }

    [Theory]
    // Narrow: hidden by the media rule, so the first toggle must OPEN it. Wide: shown by default,
    // so the same toggle must PUT IT AWAY. One flag cannot express that, which is the whole reason
    // the model carries three states.
    [InlineData(412, 915, true)]
    [InlineData(1280, 800, false)]
    public void TheRoomsToggleFollowsTheBreakpoint(int w, int h, bool hiddenToStart)
    {
        var vm = Furnished();
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();

        float Width()
        {
            var p = BanterChatApp.Presentation(w, h);
            doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
            return Walk(doc.Root)
                .Where(n => n.Node.Element?.GetAttribute("class")?.Split(' ').Contains("sidebar") == true)
                .Select(n => n.Node.Width)
                .FirstOrDefault();
        }

        var before = Width();
        Assert.Equal(hiddenToStart, before <= 0f);

        // Driven through the view model with the breakpoint the app itself uses, then measured
        // through the cascade — so this fails if the constant and the @media rule drift apart.
        vm.ToggleRooms(narrow: w <= BanterChatApp.NarrowWidth);
        doc.Refresh();
        var after = Width();
        output.WriteLine($"{w}x{h} sidebar {before:F0} -> {after:F0}");

        Assert.NotEqual(before > 0f, after > 0f);
    }

    [Fact]
    public void AnOpenedSidebarDoesNotStealTheChatsWidth()
    {
        // The reason it is an overlay and not a column. As a flex item with a width it would be
        // shrinking `.main` again — the exact fault the breakpoint exists to fix, reintroduced by
        // the control that is supposed to help.
        var vm = Furnished();
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        var p = BanterChatApp.Presentation(412, 915);

        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
        var shut = Find(doc, "main").Node.Width;

        vm.ToggleRooms(narrow: true);
        doc.Refresh();
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
        var open = Find(doc, "main").Node.Width;

        output.WriteLine($"main {shut:F0} with the rooms away, {open:F0} with them open");
        Assert.Equal(shut, open, 1);
        Assert.True(open > 0f, "the chat has no width either way");
    }
}
