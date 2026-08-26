using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The tool-grants panel (PLAN §8). Grants are the client's only reach into tools at all, so the
/// behaviour worth pinning is that nothing is sent until Save, that Save sends the whole set, and
/// that a refusal leaves the operator's selection intact instead of silently discarding it.
/// </summary>
public sealed class ToolGrantPanelTests
{
    private static readonly (string Name, string Server, string Description)[] Catalogue =
    [
        ("read_file", "fs", "Read a file"),
        ("gh_list_issues", "github", "List issues"),
        ("gh_create_issue", "github", "Open an issue"),
    ];

    private static ChatViewModel Loaded(string agent = "dagger")
    {
        var vm = new ChatViewModel();
        vm.SetToolCatalogue(Catalogue);
        vm.SelectToolAgent(agent);
        vm.SetToolGrants(agent, ["gh_list_issues"]);
        return vm;
    }

    [Fact]
    public void TheCatalogueShowsWhichToolsTheAgentHolds()
    {
        var vm = Loaded();

        var granted = vm.Model.ToolCatalog.Where(t => t.RowClass.Contains("granted")).Select(t => t.Name);

        Assert.Equal(["gh_list_issues"], granted);
        Assert.Equal(3, vm.Model.ToolCatalog.Count);
    }

    [Fact]
    public void TogglingDoesNotSendAnythingOnItsOwn()
    {
        var vm = Loaded();

        vm.ToggleTool("read_file");

        // The pending set is what Save would send; the panel has not asked the server for
        // anything yet, and the status says so.
        Assert.Equal(["gh_list_issues", "read_file"], vm.PendingGrants);
        Assert.True(vm.HasUnsavedGrants);
        Assert.Equal("Unsaved changes", vm.Model.ToolsStatus);
    }

    [Fact]
    public void TogglingTwiceReturnsToWhereItStarted()
    {
        var vm = Loaded();

        vm.ToggleTool("read_file");
        vm.ToggleTool("read_file");

        Assert.Equal(["gh_list_issues"], vm.PendingGrants);
    }

    [Fact]
    public void RevokingEverythingSendsAnEmptySetRatherThanNothing()
    {
        var vm = Loaded();

        vm.ToggleTool("gh_list_issues");

        // An empty list is a real instruction — revoke all. If the panel treated it as "no
        // change" there would be no way to take the last tool away.
        Assert.Empty(vm.PendingGrants);
        Assert.True(vm.HasUnsavedGrants);
    }

    [Fact]
    public void SavingAdoptsWhatTheServerStoredNotWhatWasAsked()
    {
        var vm = Loaded();
        vm.ToggleTool("read_file");

        // The server drops names nothing serves, so the reply is authoritative.
        vm.ToolGrantsSaved("dagger", ["gh_list_issues"]);

        Assert.False(vm.HasUnsavedGrants);
        Assert.Equal(["gh_list_issues"], vm.PendingGrants);
        Assert.Contains("Saved", vm.Model.ToolsStatus);
    }

    [Fact]
    public void ARefusalKeepsTheSelectionSoNothingIsLost()
    {
        var vm = Loaded();
        vm.ToggleTool("read_file");

        vm.ToolGrantsFailed("NOT_ADMIN: Only an admin may read or change tool grants.");

        Assert.True(vm.HasUnsavedGrants);
        Assert.Equal(["gh_list_issues", "read_file"], vm.PendingGrants);
        Assert.Contains("NOT_ADMIN", vm.Model.ToolsStatus);
    }

    [Fact]
    public void SwitchingAgentDropsUnsavedEditsRatherThanCarryingThemOver()
    {
        var vm = Loaded();
        vm.ToggleTool("read_file");

        vm.SelectToolAgent("scout");

        // Carrying an edit across agents would grant it to the wrong one on the next Save.
        Assert.False(vm.HasUnsavedGrants);
        Assert.Empty(vm.PendingGrants);
    }

    [Fact]
    public void ClosingThePanelDropsUnsavedEdits()
    {
        var vm = Loaded();
        vm.ShowToolPanel(true);
        vm.ToggleTool("read_file");

        vm.ShowToolPanel(false);

        Assert.False(vm.ToolPanelVisible);
        Assert.Equal(["gh_list_issues"], vm.PendingGrants);
    }

    [Fact]
    public void AnAgentWhoseGrantsHaveNotBeenReadShowsADashNotZero()
    {
        var vm = new ChatViewModel();
        vm.SetToolCatalogue(Catalogue);
        vm.SetAgents("#work", [("dagger", true, "chat", false), ("scout", false, "code", false)]);
        vm.SetToolGrants("dagger", ["read_file"]);

        var rows = vm.Model.ToolAgents.ToDictionary(a => a.Nick, a => a.Summary);

        // "0 of 3" for an agent nobody has looked at yet reads as "holds nothing", and invites
        // granting a second time on top of what it already has.
        Assert.Equal("1 of 3", rows["dagger"]);
        Assert.Equal("—", rows["scout"]);
    }

    [Fact]
    public void NoToolsConnectedHidesTheEntryPoint()
    {
        var vm = new ChatViewModel();

        vm.SetToolCatalogue([]);

        // A button that can only ever produce a refusal is worse than no button.
        Assert.Contains("hidden", vm.Model.ToolsButtonClass);
    }

    [Fact]
    public void ACatalogueRevealsTheEntryPoint()
    {
        var vm = new ChatViewModel();

        vm.SetToolCatalogue(Catalogue);

        Assert.DoesNotContain("hidden", vm.Model.ToolsButtonClass);
    }

    [Fact]
    public void ToolsAreGroupedByTheServerThatSuppliesThem()
    {
        var vm = Loaded();

        var order = vm.Model.ToolCatalog.Select(t => t.Server);

        // An operator grants by upstream far more often than alphabetically across all of them.
        Assert.Equal(["fs", "github", "github"], order);
    }

    [Fact]
    public void SavingWithNoAgentSelectedSaysSoInsteadOfSendingNothing()
    {
        var vm = new ChatViewModel();
        vm.SetToolCatalogue(Catalogue);
        var sent = new List<string>();
        var app = new BanterChatApp(vm)
        {
            ToolsSaveAsync = (agent, _) => { sent.Add(agent); return Task.CompletedTask; },
        };

        app.SaveTools();
        vm.ApplyPending();

        Assert.Empty(sent);
        Assert.Equal("Pick an agent first.", vm.Model.ToolsStatus);
    }

    [Fact]
    public void SaveSendsTheCompleteSetForTheSelectedAgent()
    {
        var vm = Loaded();
        vm.ToggleTool("gh_create_issue");
        (string Agent, IReadOnlyList<string> Tools)? sent = null;
        var app = new BanterChatApp(vm)
        {
            ToolsSaveAsync = (agent, tools) => { sent = (agent, tools); return Task.CompletedTask; },
        };

        app.SaveTools();

        // Wholesale, not a delta: a partial update leaves the server's idea of the grant set and
        // the operator's disagreeing, and only one of them is enforced.
        Assert.NotNull(sent);
        Assert.Equal("dagger", sent!.Value.Agent);
        Assert.Equal(["gh_create_issue", "gh_list_issues"], sent.Value.Tools);
    }

    [Fact]
    public void OpeningThePanelAsksTheHeadToFillIt()
    {
        var vm = new ChatViewModel();
        var asked = new List<string>();
        var app = new BanterChatApp(vm) { ToolsOpenAsync = a => { asked.Add(a); return Task.CompletedTask; } };

        app.OpenTools();
        vm.ApplyPending();

        Assert.True(vm.ToolPanelVisible);
        Assert.Equal([""], asked);
    }
}
