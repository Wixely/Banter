using System.Reflection;
using Banter.App;
using CupriFace.Dom;
using Xunit;

namespace Banter.App.Tests;

/// <summary>The About tab: which build this is, where it lives, and how the less obvious parts work.</summary>
public sealed class AboutPageTests
{
    private static ChatViewModel OnAbout()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.Connected("tcp://host:7770", "alice");
        vm.ShowSettingsPanel(true);
        vm.ShowSettingsSection("about");
        return vm;
    }

    [Fact]
    public void ItIsATabOnTheSettingsPage()
    {
        var vm = OnAbout();

        Assert.Contains(vm.Model.SettingsSections, s => s.Value == "about" && s.RowClass.Contains("on"));
        Assert.Equal("mgmt-field", vm.Model.AboutFieldsClass);
        Assert.Equal("mgmt-field hidden", vm.Model.YouFieldsClass);
    }

    [Fact]
    public void ItSaysWhichBuildThisIs()
    {
        var vm = OnAbout();

        Assert.Equal(ChatViewModel.VersionOf(typeof(ChatViewModel).Assembly), vm.Model.AboutVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+", vm.Model.AboutVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+", vm.Model.AboutEngine);
    }

    [Fact]
    public void ItExplainsTheRulesThatSurprisePeople()
    {
        var vm = OnAbout();

        Assert.True(vm.Model.HelpItems.Count >= 5);
        Assert.Contains(vm.Model.HelpItems, h => h.Title.Contains("delegator", StringComparison.OrdinalIgnoreCase)
                                                 || h.Text.Contains("delegator", StringComparison.OrdinalIgnoreCase));
        Assert.All(vm.Model.HelpItems, h => Assert.False(string.IsNullOrWhiteSpace(h.Text)));
    }

    [Fact]
    public void TheLinkOpensTheProjectOnGitHub()
    {
        var vm = OnAbout();
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        var opened = new List<string>();
        doc.Navigated += e => opened.Add(e.Href);
        var p = BanterChatApp.Presentation(1280, 800);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var link = Walk(doc.Root).First(n => n.Node.Element?.GetAttribute("class") == "about-link" && n.Node.Width > 0);
        doc.DispatchClick(link.X + (link.Node.Width / 2), link.Y + (link.Node.Height / 2));

        Assert.Equal(["https://github.com/Wixely/Banter"], opened);
    }

    /// <summary>
    /// The debug bundle has a button, because on a phone the slash command wrote a file into the
    /// app's own storage where nothing could reach it. It is an admin's button - the server
    /// refuses the bundle to everybody else - and whichever order admin-ness and the tab arrive in,
    /// it shows for an admin and not otherwise.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheDebugBundleButtonIsAnAdminsWhicheverCameFirst(bool adminFirst)
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.Connected("tcp://host:7770", "alice");
        vm.ShowSettingsPanel(true);
        if (adminFirst)
        {
            vm.SetIsAdmin(true);
            vm.ShowSettingsSection("about");
        }
        else
        {
            vm.ShowSettingsSection("about");
            vm.SetIsAdmin(true);
        }

        Assert.Equal("mgmt-field", vm.Model.DebugBundleClass);

        vm.SetIsAdmin(false);
        Assert.Equal("mgmt-field hidden", vm.Model.DebugBundleClass);

        vm.SetIsAdmin(true);
        vm.ShowSettingsSection("you");
        Assert.Equal("mgmt-field hidden", vm.Model.DebugBundleClass);
    }

    [Fact]
    public void TheButtonTakesABundleAndSaysWhereItWent()
    {
        var vm = OnAbout();
        vm.SetIsAdmin(true);
        var asked = 0;
        var app = new BanterChatApp(vm)
        {
            DebugBundleAsync = () =>
            {
                asked++;
                return Task.FromResult("Debug bundle saved to /sdcard/Download/banter-debug.json (41 KB).");
            },
        };
        using var doc = app.CreateDocument();
        var p = BanterChatApp.Presentation(1280, 800);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        // The button or anything painted inside it: a click lands on the innermost box and the
        // handler is on the closest .debug-bundle, as a finger's would be.
        var button = Walk(doc.Root).First(n => n.Node.Element?.Closest(".debug-bundle") is not null && n.Node.Width > 0);
        doc.DispatchClick(button.X + (button.Node.Width / 2), button.Y + (button.Node.Height / 2));
        vm.ApplyPending();

        Assert.Equal(1, asked);
        Assert.StartsWith("Debug bundle saved to /sdcard/Download/banter-debug.json", vm.Model.DebugBundleStatus);
    }

    [Theory]
    [InlineData("0.13.2+cd91672f6088e641a232ff15192134ea4a6383e1", "0.13.2 (cd91672)")]
    [InlineData("0.14.0", "0.14.0")]
    public void AVersionReadsAsAPersonWouldWriteIt(string informational, string expected)
    {
        var name = new AssemblyName("probe") { Version = new Version(9, 9, 9) };
        var builder = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(name, System.Reflection.Emit.AssemblyBuilderAccess.Run);
        builder.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informational]));

        Assert.Equal(expected, ChatViewModel.VersionOf(builder));
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
}
