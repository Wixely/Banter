using Banter.App;
using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The window is resizable, so the layout has to follow it. It did not: the app was pinned to the
/// 760px it opened at, which left dead space on a taller window and pushed the composer clean off
/// the bottom of a shorter one — the message box became unreachable rather than merely cramped.
/// </summary>
public sealed class WindowSizeTests(ITestOutputHelper output)
{
    /// <summary>Nodes with their absolute position, since <c>Y</c> is relative to the parent.</summary>
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

    private static (float Top, float Height) Find(RenderNode root, string tag)
    {
        var hit = Walk(root).FirstOrDefault(n => n.Node.Tag == tag);
        Assert.NotNull(hit.Node);
        return (hit.Top, hit.Node.Height);
    }

    private static ChatViewModel Populated()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.EnableVoice(readbackAvailable: true);
        for (var i = 0; i < 40; i++)
        {
            vm.Append("#main", "bob", $"message {i}", 0);
        }

        return vm;
    }

    [Theory]
    [InlineData(1100, 760)]
    [InlineData(1400, 1000)]
    [InlineData(900, 620)]
    [InlineData(800, 500)]
    public void TheComposerStaysOnScreenAtAnyWindowSize(int width, int height)
    {
        var app = new BanterChatApp(Populated());
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(width, height);

        var (top, composerHeight) = Find(doc.Root, "cupri-textarea");
        output.WriteLine($"{width}x{height}: composer top={top:F0} bottom={top + composerHeight:F0}");

        // The box you type into is the one control that must never be off the bottom of a window.
        Assert.True(top + composerHeight <= height,
            $"composer runs to {top + composerHeight:F0} in a window {height} tall.");
    }

    [Fact]
    public void TheTimelineTakesTheSpaceTheWindowActuallyHas()
    {
        var app = new BanterChatApp(Populated());
        using var doc = app.CreateDocument();

        doc.BuildDisplayList(1100, 760);
        var (_, atDesignSize) = Find(doc.Root, "cupri-virtual");

        doc.BuildDisplayList(1100, 1000);
        var (_, taller) = Find(doc.Root, "cupri-virtual");

        doc.BuildDisplayList(1100, 560);
        var (_, shorter) = Find(doc.Root, "cupri-virtual");

        output.WriteLine($"timeline heights: 760 -> {atDesignSize:F0}, 1000 -> {taller:F0}, 560 -> {shorter:F0}");

        // Growing the window should show more conversation, not more empty background.
        Assert.True(taller > atDesignSize + 200, $"a 240px taller window gained {taller - atDesignSize:F0}px of timeline.");
        Assert.True(shorter < atDesignSize - 150, $"a 200px shorter window lost {atDesignSize - shorter:F0}px of timeline.");
    }

    [Fact]
    public void TheToolOverlayCoversTheWholeWindow()
    {
        var vm = Populated();
        vm.SetToolCatalogue([("read_file", "fs", "Read a file")]);
        vm.ShowToolPanel(true);

        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1400, 1000);

        var panel = Walk(doc.Root)
            .Where(n => n.Node.Width > 1000 && n.Node.Height > 800)
            .ToList();

        // An overlay fixed at the window's opening size leaves the app showing around its edges,
        // which reads as a rendering fault rather than a panel.
        Assert.NotEmpty(panel);
    }
}
