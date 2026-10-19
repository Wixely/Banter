using Banter.App;
using CupriFace.Dom;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// Choosing, per room, which agents act on your messages - and being warned in the composer when
/// one of them is a third party.
/// </summary>
public sealed class RecipientPickTests
{
    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.AddRoom("#other");
        vm.SwitchTo("#main");
        vm.SetAgents("#main", [("local", true, "chat", true), ("claude", false, "web", false)]);
        return vm;
    }

    private static AgentRow Row(ChatViewModel vm, string nick) => vm.Model.Agents.Single(a => a.Nick == nick);

    [Fact]
    public void ByDefaultTheDelegatorIsTickedAndNothingIsSent()
    {
        var vm = Room();

        Assert.Equal("pick on", Row(vm, "local").PickClass);
        Assert.Equal("pick", Row(vm, "claude").PickClass);
        Assert.Equal("pick-clear hidden", vm.Model.PickClearClass);

        // The default goes as no list at all, so the room's own dispatch decides.
        Assert.Null(vm.RecipientsFor("#main"));
    }

    [Fact]
    public void TickingAnotherAgentAddsItAndResetGoesBack()
    {
        var vm = Room();

        vm.ToggleRecipient("claude");
        Assert.Equal(["claude", "local"], vm.RecipientsFor("#main"));
        Assert.Equal("pick on", Row(vm, "claude").PickClass);
        Assert.Equal("pick-clear", vm.Model.PickClearClass);
        Assert.True(vm.TakeRecipientsChanged());

        vm.ToggleRecipient("local");
        Assert.Equal(["claude"], vm.RecipientsFor("#main"));

        vm.ClearRecipients();
        Assert.Null(vm.RecipientsFor("#main"));
        Assert.Equal("pick on", Row(vm, "local").PickClass);
    }

    [Fact]
    public void UntickingEveryoneMeansNobodyRatherThanTheDefault()
    {
        var vm = Room();
        vm.ToggleRecipient("local");

        Assert.Equal([], vm.RecipientsFor("#main"));
    }

    [Fact]
    public void TheChoiceIsPerRoom()
    {
        var vm = Room();
        vm.ToggleRecipient("claude");

        vm.SwitchTo("#other");
        Assert.Null(vm.RecipientsFor("#other"));
        Assert.Equal("pick-clear hidden", vm.Model.PickClearClass);
    }

    [Fact]
    public void TheComposerWarnsWhenAThirdPartyWouldGetTheMessage()
    {
        var vm = Room();
        vm.RefreshEgress();
        Assert.Equal("egress-warn hidden", vm.Model.EgressClass);

        // Named with an @.
        vm.Model.Composer = "@claude what do you think?";
        Assert.True(vm.RefreshEgress());
        Assert.Equal("egress-warn", vm.Model.EgressClass);
        Assert.Contains("claude", vm.Model.EgressTip);

        // Not a mention: "@claudette" is somebody else.
        vm.Model.Composer = "@claudette hi";
        vm.RefreshEgress();
        Assert.Equal("egress-warn hidden", vm.Model.EgressClass);

        // Ticked.
        vm.ToggleRecipient("claude");
        vm.RefreshEgress();
        Assert.Equal("egress-warn", vm.Model.EgressClass);
    }

    [Fact]
    public void AThirdPartyDelegatorIsWarnedAboutToo()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SetAgents("#main", [("claude", false, "web", true)]);

        vm.RefreshEgress();

        Assert.Equal("egress-warn", vm.Model.EgressClass);
    }

    [Fact]
    public void TheChoiceSurvivesBeingSavedAndLoaded()
    {
        var vm = Room();
        vm.ToggleRecipient("claude");
        var saved = vm.SavedRecipients;

        var again = Room();
        again.LoadRecipients(saved);

        Assert.Equal(["claude", "local"], again.RecipientsFor("#main"));
        Assert.Equal("pick on", Row(again, "claude").PickClass);
    }

    [Fact]
    public void TheSettingsFileKeepsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"banter-recipients-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new BanterSettings { Recipients = new() { ["#main"] = ["claude", "local"], ["#quiet"] = [] } };
            Assert.True(settings.TrySave(path));

            var loaded = BanterSettings.Load(path);
            Assert.Equal(["claude", "local"], loaded.Recipients["#main"]);
            Assert.Empty(loaded.Recipients["#quiet"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TickingInTheRosterSavesTheChoice()
    {
        var vm = Room();
        IReadOnlyDictionary<string, string[]>? saved = null;
        var app = new BanterChatApp(vm) { RecipientsChanged = s => saved = s };
        using var doc = app.CreateDocument();
        var p = BanterChatApp.Presentation(1280, 800);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var box = Walk(doc.Root).First(n => n.Node.Element?.GetAttribute("data-pick") == "claude" && n.Node.Width > 0);
        doc.DispatchClick(box.X + (box.Node.Width / 2), box.Y + (box.Node.Height / 2));

        Assert.NotNull(saved);
        Assert.Equal(["claude", "local"], saved!["#main"]);
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
