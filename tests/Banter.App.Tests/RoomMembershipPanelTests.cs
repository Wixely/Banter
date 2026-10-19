using Banter.App;
using CupriFace.Dom;
using Xunit;

namespace Banter.App.Tests;

/// <summary>An admin's controls for who is in a room: a way out for each member, a way in for
/// the people and agents they can see elsewhere.</summary>
public sealed class RoomMembershipPanelTests
{
    private static ChatViewModel Rooms(bool admin)
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.SetIsAdmin(admin);
        vm.AddRoom("#main");
        vm.AddRoom("#side");
        vm.SetAgents("#main", [("local", true, "chat", true), ("scout", true, "notes", false)]);
        vm.SetRoomUsers("#main", [("alice", "o"), ("bob", "")]);
        vm.SetAgents("#side", [("scout", true, "notes", true)]);
        vm.SetRoomUsers("#side", [("alice", "o")]);
        vm.SwitchTo("#side");
        vm.Connected("tcp://host:7770", "alice");
        return vm;
    }

    [Fact]
    public void AnAdminCanTakeEveryoneButThemselvesOut()
    {
        var vm = Rooms(admin: true);
        vm.SwitchTo("#main");

        Assert.All(vm.Model.Agents, a => Assert.Equal("member-x", a.RemoveClass));
        Assert.Equal("member-x", vm.Model.Users.Single(u => u.Nick == "bob").RemoveClass);
        Assert.Equal("no-close", vm.Model.Users.Single(u => u.Nick == "alice").RemoveClass);
    }

    [Fact]
    public void AnAdminIsOfferedWhoeverIsElsewhereButNotHere()
    {
        var vm = Rooms(admin: true);

        Assert.Equal("addable", vm.Model.AddableClass);
        Assert.Equal(["local", "bob"], vm.Model.Addable.Select(a => a.Nick));
        Assert.Equal(["agent", "person"], vm.Model.Addable.Select(a => a.Kind));
    }

    [Fact]
    public void SomebodyWhoIsNotAnAdminIsOfferedNeither()
    {
        var vm = Rooms(admin: false);

        Assert.Equal("addable hidden", vm.Model.AddableClass);
        Assert.Empty(vm.Model.Addable);
        Assert.All(vm.Model.Agents, a => Assert.Equal("no-close", a.RemoveClass));
    }

    [Fact]
    public void TheControlsAreTheCommands()
    {
        var vm = Rooms(admin: true);
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

        Click(doc, "data-member-add", "local");
        Click(doc, "data-member-remove", "scout");

        Assert.Equal(["#side /add local", "#side /remove scout"], commands);
    }

    private static void Click(CupriFace.CupriDocument doc, string attribute, string value)
    {
        // Laid out afresh each time: a click that is handled refreshes the document, and the tree
        // it leaves is not laid out until the next frame.
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
