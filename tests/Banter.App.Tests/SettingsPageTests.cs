using Banter.App;
using CupriFace;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The settings page, and the zoom it carries.
///
/// <para>Zoom is the one setting here so far, and it is the one that has to be right: it changes
/// how everything else is laid out, it can be driven from outside this page (Ctrl and the wheel),
/// and it is remembered, so a value that reaches the document but not the head is a preference
/// that quietly resets every launch.</para>
/// </summary>
public sealed class SettingsPageTests(ITestOutputHelper output)
{
    private const int Width = 1240;
    private const int Height = 800;

    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        return vm;
    }

    [Fact]
    public void TheSettingsButtonIsThereForEveryone()
    {
        // Unlike the agents and users pages, this one is about this machine, not the server, so
        // it is not an admin's page.
        var member = Room();
        member.SetIsAdmin(false);
        Assert.DoesNotContain("hidden", member.Model.SettingsButtonClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRailButtonOpensSettingsShowingTheZoomInForce()
    {
        var vm = Room();
        var app = new BanterChatApp(vm) { InitialZoom = 1.35f };

        using var doc = app.CreateDocument();
        doc.Refresh();
        doc.BuildDisplayList(Width, Height);

        var (x, y) = PointOn(doc, "[data-settings-open]");
        doc.DispatchClick(x, y, 1);

        Assert.True(vm.SettingsPanelOpen);
        Assert.Equal("135%", vm.Model.ZoomLabel);
    }

    [Fact]
    public void OpeningSettingsLeavesTheAdminPages()
    {
        // Driven on the view model rather than through a click: an open management page covers
        // the whole window, rail included, so there is no rail button to click while one is up.
        var vm = Room();
        vm.SetIsAdmin(true);
        vm.ShowAgentsPanel(true);

        vm.ShowSettingsPanel(true);

        Assert.True(vm.SettingsPanelOpen);
        Assert.False(vm.AgentsPanelOpen);
    }

    [Fact]
    public void ChoosingAZoomReachesTheDocumentAndTheHead()
    {
        var vm = Room();
        var saved = new List<float>();
        var app = new BanterChatApp(vm) { ZoomChanged = z => saved.Add(z) };

        using var doc = app.CreateDocument();
        vm.ShowSettingsPanel(true);
        vm.SetZoom(1f);

        // Class bindings are re-read on Refresh, so a model change before a build needs one —
        // otherwise the page is laid out from the state it had at creation.
        doc.Refresh();
        doc.BuildDisplayList(Width, Height);

        var (x, y) = PointOn(doc, "[data-zoom=\"1.35\"]");
        doc.DispatchClick(x, y, 1);

        output.WriteLine($"document zoom {doc.Zoom}, label {vm.Model.ZoomLabel}, saved [{string.Join(", ", saved)}]");

        // All three, because any one of them alone is a bug: the document is what actually
        // scales, the label is what the page claims, and the head is what remembers.
        Assert.Equal(1.35f, doc.Zoom, 3);
        Assert.Equal("135%", vm.Model.ZoomLabel);
        Assert.Contains(1.35f, saved);
    }

    [Fact]
    public void TheStartingZoomIsWhateverWasRemembered()
    {
        var vm = Room();
        var app = new BanterChatApp(vm) { InitialZoom = 0.9f };

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        Assert.Equal(0.9f, doc.Zoom, 3);
        Assert.Equal("90%", vm.Model.ZoomLabel);
    }

    [Fact]
    public void ZoomingFromOutsideThePageStillUpdatesIt()
    {
        var vm = Room();
        var saved = new List<float>();
        var app = new BanterChatApp(vm) { ZoomChanged = z => saved.Add(z) };

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        // Ctrl and the wheel, or the keyboard, go straight to the document. The page has to
        // follow rather than be the only thing that knows.
        doc.ZoomIn();

        Assert.True(doc.Zoom > 1f);
        Assert.Equal($"{Math.Round(doc.Zoom * 100)}%", vm.Model.ZoomLabel);
        Assert.Contains(doc.Zoom, saved);
    }

    [Fact]
    public void ZoomActuallyChangesHowLargeThingsArePainted()
    {
        // The gate on all of it: a zoom that is stored, labelled and remembered but does not
        // scale the picture would pass every other test here.
        var small = LogoHeight(1f);
        var large = LogoHeight(1.6f);
        output.WriteLine($"logo painted {small}px at 100%, {large}px at 160%");

        Assert.True(large > small * 1.3f,
            $"Zooming to 160% painted the logo {large}px against {small}px at 100%.");
    }

    /// <summary>
    /// The logo, because it is anchored to the top-left corner and stays in the window at every
    /// zoom. Anything further down the page leaves the viewport as the scale rises — measuring
    /// the composer here reported zero at 2x and looked exactly like zoom not working at all.
    /// </summary>
    private static float LogoHeight(float zoom)
    {
        var vm = Room();
        using var doc = new BanterChatApp(vm) { InitialZoom = zoom }.CreateDocument();
        doc.Refresh();
        doc.BuildDisplayList(Width, Height);

        float min = float.MaxValue, max = -1;
        for (var y = 0f; y < Height; y += 1)
        {
            for (var x = 0f; x < 200; x += 2)
            {
                if (doc.HitTest(x, y)?.Element?.Closest(".logo") is null)
                {
                    continue;
                }

                min = Math.Min(min, y);
                max = Math.Max(max, y);
            }
        }

        return max < 0 ? 0 : max - min;
    }

    private static (float X, float Y) PointOn(CupriDocument doc, string selector)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0f; y < Height; y += 2)
        {
            for (var x = 0f; x < Width; x += 2)
            {
                if (doc.HitTest(x, y)?.Element?.Closest(selector) is null)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < 0)
        {
            throw new Xunit.Sdk.XunitException($"nothing painted matches {selector}");
        }

        return ((minX + maxX) / 2, (minY + maxY) / 2);
    }
}
