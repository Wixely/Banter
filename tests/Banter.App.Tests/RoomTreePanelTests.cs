using Banter.App;
using Banter.Protocol;
using CupriFace.Dom;
using Xunit;

namespace Banter.App.Tests;

/// <summary>The room list drawn as the tree it is, and the panel that says - and for an admin
/// changes - where a room sits and who may draw on it.</summary>
public sealed class RoomTreePanelTests
{
    private static ChatViewModel Tree(bool admin)
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.SetIsAdmin(admin);
        vm.AddRoom("#main");
        vm.AddRoom("#deep");
        vm.AddRoom("#side");
        vm.AddRoom("#other");
        vm.SetRoomListing([
            ("#main", null, 3, RoomDisclosure.Members),
            ("#side", "#main", 2, RoomDisclosure.Children),
            ("#deep", "#side", 1, RoomDisclosure.Isolated),
            ("#other", null, 1, RoomDisclosure.Global),
        ]);
        vm.SwitchTo("#side");
        vm.Connected("tcp://host:7770", "alice");
        return vm;
    }

    [Fact]
    public void RoomsAreListedInTreeOrderIndentedByDepth()
    {
        var vm = Tree(admin: true);

        Assert.Equal(["#main", "#side", "#deep", "#other"], vm.Model.Rooms.Select(r => r.Name));
        var deep = vm.Model.Rooms.Single(r => r.Name == "#deep").Label;
        var side = vm.Model.Rooms.Single(r => r.Name == "#side").Label;
        Assert.True(deep.IndexOf('#') > side.IndexOf('#'), $"'{deep}' is not indented past '{side}'");
    }

    [Fact]
    public void AGlobalRoomIsTaggedOnItsTab()
    {
        var vm = Tree(admin: false);

        Assert.Equal("room-global", vm.Model.Rooms.Single(r => r.Name == "#other").GlobalClass);
        Assert.Equal("no-close", vm.Model.Rooms.Single(r => r.Name == "#main").GlobalClass);
    }

    [Fact]
    public void AnAdminChoosesWhereItSitsButNotInsideItself()
    {
        var vm = Tree(admin: true);
        vm.ShowRoomPanel(true);

        Assert.Equal("room-edit", vm.Model.RoomPanelEditClass);
        var places = vm.Model.ParentChoices.Select(c => c.Value).ToList();
        Assert.Equal(["", "#main", "#other"], places);
        Assert.Contains("selected", vm.Model.ParentChoices.Single(c => c.Value == "#main").RowClass);
        Assert.Contains("selected", vm.Model.DisclosureChoices.Single(c => c.Value == "children").RowClass);
    }

    [Fact]
    public void GlobalIsWarnedAbout()
    {
        var vm = Tree(admin: true);
        vm.SwitchTo("#other");
        vm.ShowRoomPanel(true);

        Assert.Equal("room-warning", vm.Model.DisclosureWarningClass);
        Assert.Contains("warn", vm.Model.DisclosureChoices.Single(c => c.Value == "global").RowClass);
    }

    [Fact]
    public void SomebodyWhoCannotChangeItStillReadsIt()
    {
        var vm = Tree(admin: false);
        vm.ShowRoomPanel(true);

        Assert.Equal("room-edit hidden", vm.Model.RoomPanelEditClass);
        Assert.Equal("Inside #main", vm.Model.RoomPanelPlace);
        Assert.StartsWith("Children", vm.Model.RoomPanelDisclosure);
    }

    [Fact]
    public void ThePanelsChoicesAreTheCommands()
    {
        var vm = Tree(admin: true);
        vm.ShowRoomPanel(true);
        var commands = new List<string>();
        var app = new BanterChatApp(vm)
        {
            CommandAsync = (room, text) =>
            {
                commands.Add($"{room} {text}");
                return Task.CompletedTask;
            },
        };
        using var doc = app.CreateDocument();

        Click(doc, "data-disclosure", "isolated");
        Click(doc, "data-parley", "open");
        // Last, because moving the room closes the panel.
        Click(doc, "data-room-parent", "");

        Assert.Equal(["#side /disclosure isolated", "#side /parley open", "#side /move #side top"], commands);
    }

    [Fact]
    public void TheParleyIsPairsUntilTheServerSaysOtherwise()
    {
        var vm = Tree(admin: true);
        vm.ShowRoomPanel(true);
        Assert.Contains("selected", vm.Model.ParleyChoices.Single(c => c.Value == "pairs").RowClass);

        vm.SetRoomParley("#side", RoomParley.None);
        Assert.Contains("selected", vm.Model.ParleyChoices.Single(c => c.Value == "none").RowClass);
        Assert.DoesNotContain("selected", vm.Model.ParleyChoices.Single(c => c.Value == "pairs").RowClass);

        // And somebody who cannot change it reads it.
        var reader = Tree(admin: false);
        reader.SetRoomParley("#side", RoomParley.Open);
        reader.ShowRoomPanel(true);
        Assert.Contains("Open", reader.Model.RoomPanelParley, StringComparison.Ordinal);
    }

    [Fact]
    public void NewRoomStartsTheCommand()
    {
        var vm = Tree(admin: false);
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();

        Click(doc, "data-new-room", "1");

        Assert.Equal("/create #", vm.Model.Composer);
    }

    private static void Click(CupriFace.CupriDocument doc, string attribute, string value)
    {
        var p = BanterChatApp.Presentation(1280, 800);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
        var hit = Walk(doc.Root).First(n => n.Node.Element?.GetAttribute(attribute) == value && n.Node.Width > 0);
        doc.DispatchClick(hit.X + (hit.Node.Width / 2), hit.Y + (hit.Node.Height / 2));
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
