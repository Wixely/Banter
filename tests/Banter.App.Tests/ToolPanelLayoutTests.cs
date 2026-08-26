using Banter.App;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The grants panel is an overlay, and "overlay" is a claim about CupriFace's CSS subset rather
/// than something the markup guarantees. These tests render and read pixels, because a panel that
/// silently became a third column would still look plausible in the markup and wrong on screen.
/// </summary>
public sealed class ToolPanelLayoutTests(ITestOutputHelper output)
{
    private const int Width = 1100;
    private const int Height = 760;

    private static ChatViewModel Populated()
    {
        var vm = new ChatViewModel();
        vm.SetNick("admin");
        vm.AddRoom("#main");
        vm.Append("#main", "dagger", "a line of chat that must be covered", 0);
        vm.SetToolCatalogue(
        [
            ("read_file", "fs", "Read a file"),
            ("gh_list_issues", "github", "List issues"),
        ]);
        vm.SelectToolAgent("dagger");
        vm.SetToolGrants("dagger", ["gh_list_issues"]);
        return vm;
    }

    private static SKColor PixelAt(BanterChatApp app, int x, int y)
    {
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);
        var pixels = doc.RenderToPixels(Width, Height, SKColors.Black);
        var offset = ((y * Width) + x) * 4;
        return new SKColor(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    [Fact]
    public void TheOpenPanelCoversTheSidebarRatherThanSittingBesideIt()
    {
        var vm = Populated();
        var app = new BanterChatApp(vm);

        var closed = PixelAt(app, 20, 20);
        vm.ShowToolPanel(true);
        var open = PixelAt(app, 20, 20);

        output.WriteLine($"closed {closed}, open {open}");

        // Measured: #1b1e24 closed (the sidebar), #0d0f13 open (the panel's backdrop). Naming the
        // colours rather than just asserting they differ is what catches the overlay quietly
        // becoming a third column — which would still change this pixel, and still be wrong.
        Assert.Equal(new SKColor(0x1b, 0x1e, 0x24), closed);
        Assert.Equal(new SKColor(0x0d, 0x0f, 0x13), open);
    }

    [Fact]
    public void TheClosedPanelChangesNothing()
    {
        var vm = Populated();
        var app = new BanterChatApp(vm);

        var before = PixelAt(app, 20, 20);
        vm.ShowToolPanel(true);
        vm.ShowToolPanel(false);
        var after = PixelAt(app, 20, 20);

        Assert.Equal(before, after);
    }

    [Fact]
    public void ThePanelLaysOutAndPaintsWithAFullCatalogue()
    {
        var vm = Populated();
        for (var i = 0; i < 400; i++)
        {
            // A real MCPHub aggregates several hundred tools; the list is virtualized for it.
            vm.SetToolCatalogue(Enumerable.Range(0, 400)
                .Select(n => ($"tool_{n}", n % 3 == 0 ? "github" : "sql", $"Does thing {n}")));
        }

        vm.ShowToolPanel(true);
        var app = new BanterChatApp(vm);

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);
        var pixels = doc.RenderToPixels(Width, Height, SKColors.Black);

        Assert.Equal(Width * Height * 4, pixels.Length);
        Assert.Contains(pixels, b => b != 0);
    }
}
