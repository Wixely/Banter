using Banter.App;
using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Nothing runs off the side of a phone.
///
/// <para>Horizontal overflow is the failure mode of a desktop layout on a small screen, and it is
/// the one that leaves no trace on screen: a column past the right edge is not clipped, not
/// scrolled to and not drawn — it is simply not there, and the part of the interface it held is
/// unreachable with nothing to say so. <c>CF0072</c> (CupriFace 0.24.0) is the check that sees it.
/// </para>
/// </summary>
public sealed class PhoneFitTests(ITestOutputHelper output)
{
    // Density-independent pixels, which is what the Android host hands Present after dividing by
    // density. A mid-size phone portrait, the same phone landscape, and a small tablet.
    private static readonly (int W, int H)[] Screens = [(412, 915), (915, 412), (800, 1280)];

    /// <summary>Every page on every screen — the cross product, because a page that only breaks in
    /// landscape is still broken.</summary>
    public static TheoryData<string, int, int> Every
    {
        get
        {
            var data = new TheoryData<string, int, int>();
            foreach (var page in PageNames)
            {
                foreach (var (w, h) in Screens)
                {
                    data.Add(page, w, h);
                }
            }

            return data;
        }
    }

    private static ChatViewModel Furnished()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.Connected("tcp://host:7770", "alice");
        vm.SetIsAdmin(true);
        vm.Append("#main", "dagger", "hello", 0);
        return vm;
    }

    /// <summary>
    /// Every screen this app has, not just the one it opens on. The four management pages share a
    /// shell, and it broke on a phone in both of the ways there are: agents, users and work left
    /// <c>.mgmt-empty</c> with no width at all, and settings ran 195px past the right-hand edge.
    /// None of that was reachable from the chat screen, which is the only one the first version of
    /// this test looked at.
    /// </summary>
    private static readonly string[] PageNames =
        ["chat", "connect", "tools", "agents", "users", "work", "settings"];

    public static TheoryData<string> Pages
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var page in PageNames)
            {
                data.Add(page);
            }

            return data;
        }
    }

    private static BanterChatApp Showing(string page)
    {
        var vm = Furnished();
        switch (page)
        {
            case "chat": break;
            case "connect": vm.ShowConnect("", ""); break;
            case "tools": vm.ShowToolPanel(true); break;
            case "agents": vm.ShowAgentsPanel(true); break;
            case "users": vm.ShowUsersPanel(true); break;
            case "work": vm.ShowWorkPanel(true); break;
            case "settings":
                vm.SetVoiceSettings(
                    "local", "en", "dagger", "http://localhost:1234", "localhost:10200",
                    [("aiden", "Aiden"), ("bree", "Bree")], new Dictionary<string, string>());
                vm.ShowSettingsPanel(true);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(page), page, "no such page");
        }

        return new BanterChatApp(vm);
    }

    /// <summary>
    /// The two ways a box can be on screen and hold nothing you can see. <c>CF0072</c> is the one
    /// that runs off the side; <c>CF0071</c> is the one that was actually happening here — a box
    /// squeezed to no width at all, which is what a flex row does to its children rather than
    /// letting them overflow.
    /// </summary>
    private static readonly string[] Invisible = ["CF0071", "CF0072"];

    [Theory]
    [MemberData(nameof(Every))]
    public void NothingIsSqueezedOutOfExistence(string page, int w, int h)
    {
        var app = Showing(page);
        var report = CupriDoctor.Check(app.Html, app.Css, width: w, height: h, model: app.Model);

        var lost = report.Findings.Where(f => Invisible.Contains(f.Code)).ToList();
        foreach (var f in lost)
        {
            output.WriteLine(f.ToString());
        }

        // This is what it said before the narrow-window rules existed, at every one of these sizes:
        //   CF0071: <div class='main'> laid out 0x915 but has content inside it, so none of it is
        //   visible.
        // The chat pane. Not clipped and not scrolled past — zero pixels wide, because `.app` is a
        // flex row and the rail, sidebar and roster shrank in proportion until there was nothing
        // left to give it.
        Assert.True(lost.Count == 0, $"{lost.Count} thing(s) invisible on '{page}' at {w}x{h}");
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void TheDesignSizeStaysClean(string page)
    {
        // The size the four-column layout was drawn for. Here as a control: a phone finding is
        // only interesting if this one is quiet, otherwise the layout is broken everywhere and the
        // width is not what is wrong with it.
        var app = Showing(page);
        var report = CupriDoctor.Check(
            app.Html, app.Css,
            width: (int)BanterChatApp.DesignWidth, height: (int)BanterChatApp.DesignHeight,
            model: app.Model);

        var lost = report.Findings.Where(f => Invisible.Contains(f.Code)).ToList();
        foreach (var f in lost)
        {
            output.WriteLine(f.ToString());
        }

        Assert.Empty(lost);
    }
}
