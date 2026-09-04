using Banter.App;
using SkiaSharp;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The roster panel (PLAN §8a made visible). Delegation was previously only observable by reading
/// the timeline; these cover the parts a human relies on to know who is in the room.
/// </summary>
public sealed class AgentRosterTests
{
    private static ChatViewModel Room(string room = "#main")
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom(room);
        return vm;
    }

    private static (string, bool, string, bool) Agent(
        string nick, bool local = true, string skills = "chat", bool delegator = false) =>
        (nick, local, skills, delegator);

    [Fact]
    public void HumansAndAgentsShareTheRosterSeparatedByTheirLabels()
    {
        var vm = Room();

        vm.SetAgents("#main", [Agent("dagger")]);
        vm.SetRoomUsers("#main", [("alice", "o"), ("bob", "")]);

        // One list, two labelled sections: the heading is what says which kind of thing each row
        // is, so both headings show only while their section has rows.
        Assert.Equal("dagger", Assert.Single(vm.Model.Agents).Nick);
        Assert.Equal(["alice", "bob"], vm.Model.Users.Select(u => u.Nick));
        Assert.DoesNotContain("hidden", vm.Model.RosterAgentsTitleClass, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", vm.Model.RosterUsersTitleClass, StringComparison.Ordinal);

        // An op wears a badge the way the delegator wears one; everyone else wears nothing.
        Assert.Equal("op", vm.Model.Users.Single(u => u.Nick == "alice").Badge);
        Assert.Equal("", vm.Model.Users.Single(u => u.Nick == "bob").Badge);
    }

    [Fact]
    public void AnEmptySectionTakesItsHeadingWithIt()
    {
        var vm = Room();

        // A room of only humans: no "Agents" heading over nothing.
        vm.SetRoomUsers("#main", [("alice", "")]);
        Assert.Contains("hidden", vm.Model.RosterAgentsTitleClass, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", vm.Model.RosterUsersTitleClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsersSectionFollowsTheActiveRoomLikeTheAgentsDo()
    {
        var vm = Room();
        vm.AddRoom("#other");
        vm.SetRoomUsers("#main", [("alice", "")]);
        vm.SetRoomUsers("#other", [("carol", "")]);

        Assert.Equal("alice", Assert.Single(vm.Model.Users).Nick);

        vm.SwitchTo("#other");
        Assert.Equal("carol", Assert.Single(vm.Model.Users).Nick);
    }

    [Fact]
    public void AFrontierAgentIsMarkedDistinctlyFromALocalOne()
    {
        var vm = Room();

        vm.SetAgents("#main", [Agent("local"), Agent("claude", local: false, skills: "web")]);

        var localRow = vm.Model.Agents.Single(a => a.Nick == "local");
        var frontierRow = vm.Model.Agents.Single(a => a.Nick == "claude");

        Assert.Equal("local", localRow.Locality);
        Assert.Equal("agent", localRow.RowClass);

        // Whether a third party is in the room has to be visible without reading the nick and
        // remembering which is which.
        Assert.Equal("frontier", frontierRow.Locality);
        Assert.Contains("frontier", frontierRow.RowClass);
    }

    [Fact]
    public void TheDelegatorIsLabelledAndHighlighted()
    {
        var vm = Room();

        vm.SetAgents("#main", [Agent("local", delegator: true), Agent("claude", local: false)]);

        var row = vm.Model.Agents.Single(a => a.Nick == "local");
        Assert.Equal("delegator", row.Role);
        Assert.Contains("delegator", row.RowClass);
        Assert.Equal("local", vm.Model.Delegator);
    }

    [Fact]
    public void ARoomWithNoDelegatorSaysSoRatherThanShowingNothing()
    {
        var vm = Room();

        // All-frontier rooms elect nobody; the header must explain the silence.
        vm.SetAgents("#main", [Agent("claude", local: false)]);

        Assert.Equal("no delegator", vm.Model.Delegator);
    }

    [Fact]
    public void TheRosterFollowsTheActiveRoom()
    {
        var vm = Room();
        vm.AddRoom("#other");
        vm.SetAgents("#main", [Agent("local", delegator: true)]);
        vm.SetAgents("#other", [Agent("scout", local: false)]);

        Assert.Equal("local", Assert.Single(vm.Model.Agents).Nick);

        vm.SwitchTo("#other");
        Assert.Equal("scout", Assert.Single(vm.Model.Agents).Nick);
        Assert.Equal("no delegator", vm.Model.Delegator);
    }

    [Fact]
    public void AnUpdatedRosterReplacesTheOldOneRatherThanAppending()
    {
        var vm = Room();
        vm.SetAgents("#main", [Agent("local"), Agent("claude", local: false)]);

        vm.SetAgents("#main", [Agent("local", delegator: true)]);

        // A departed agent must disappear, not linger as a stale entry.
        Assert.Equal("local", Assert.Single(vm.Model.Agents).Nick);
    }

    [Fact]
    public void AnEgressAnnouncementIsStyledApartFromOrdinaryChatter()
    {
        var vm = Room();

        var ordinary = vm.Append("#main", "dagger", "routing this now", 0);
        var egress = vm.Append("#main", "dagger", "[egress] sending this to claude, a third-party agent.", 0);

        Assert.Equal("line", ordinary.RowClass);
        Assert.Equal("line egress", egress.RowClass);
    }

    [Fact]
    public void TheAppRendersWithARosterPresent()
    {
        var vm = Room();
        vm.SetAgents("#main", [Agent("local", delegator: true), Agent("claude", local: false, skills: "web, github")]);
        vm.SetDispatchMode("#main", "delegated");
        vm.Append("#main", "alice", "hello", 0);

        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);
        var pixels = doc.RenderToPixels(1100, 760, SKColors.Black);

        Assert.Contains(pixels, b => b != 0);
    }
}
