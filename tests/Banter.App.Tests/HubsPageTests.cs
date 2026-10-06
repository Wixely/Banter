using Banter.App;
using Banter.Protocol;
using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The hubs page: where the tools come from, and what the HUB says each agent may call.
///
/// <para>The distinction is the whole point of the page. The tools panel shows what Banter
/// grants; a hub enforcing for itself may disagree, and when it does the hub wins (PLAN 8c). So
/// these check that what is on screen is attributed to the hub and never to this server, and that
/// a hub which cannot be reached says so rather than reading as a hub with nothing in it.</para>
/// </summary>
public sealed class HubsPageTests(ITestOutputHelper output)
{
    private static ChatViewModel Admin()
    {
        var vm = new ChatViewModel();
        vm.SetNick("root");
        vm.AddRoom("#main");
        vm.SetIsAdmin(true);
        return vm;
    }

    private static RenderNode? Find(RenderNode node, string cls)
    {
        if (node.Element?.GetAttribute("class") is { } c
            && c.Split(' ').Contains(cls, StringComparer.Ordinal))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (Find(child, cls) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<string> Texts(RenderNode node)
    {
        if (node.Text is { Length: > 0 } t)
        {
            yield return t;
        }

        foreach (var child in node.Children)
        {
            foreach (var text in Texts(child))
            {
                yield return text;
            }
        }
    }

    [Fact]
    public void TheRailButtonIsAdminOnly()
    {
        var member = new ChatViewModel();
        member.SetNick("nell");
        member.SetIsAdmin(false);
        Assert.Contains("hidden", member.Model.HubsButtonClass, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", Admin().Model.HubsButtonClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListSaysHowBigEachHubIsAndWhetherItIsAnswering()
    {
        var vm = Admin();
        vm.SetHubs([
            Hubs.Sample,
            new HubPayload("stale", "Old hub", "Faulted", "401 from /mcp", 0, false, []),
        ]);

        var rows = vm.Model.AdminHubs;
        output.WriteLine(string.Join(" | ", rows.Select(r => $"{r.Name}: {r.Detail} / {r.State}")));

        Assert.Equal(2, rows.Count);
        Assert.Equal("17 tools \u00b7 2 agents", rows[0].Detail);
        Assert.Equal("connected", rows[0].State);

        // The one that has stopped is the one worth noticing, so it is the one marked.
        Assert.Equal("faulted", rows[1].State);
        Assert.Contains("pending", rows[1].StateClass, StringComparison.Ordinal);
        Assert.DoesNotContain("pending", rows[0].StateClass, StringComparison.Ordinal);
        Assert.Equal("2 hubs", vm.Model.HubsStatus);
    }

    /// <summary>Singular and plural both, because "1 tools" is the sort of thing that survives
    /// for years on a page nobody reads twice.</summary>
    [Fact]
    public void OneOfSomethingReadsAsOne()
    {
        var vm = Admin();
        vm.SetHubs([new HubPayload("solo", "Solo", "Connected", "", 1, true, [Hubs.Agent("scribe")])]);

        Assert.Equal("1 tool \u00b7 1 agent", vm.Model.AdminHubs[0].Detail);
        Assert.Equal("1 hub", vm.Model.HubsStatus);
    }

    [Fact]
    public void ThePaneAttributesTheGrantsToTheHub()
    {
        var vm = Admin();
        vm.SetHubs([Hubs.Sample]);
        vm.SelectHub("mcphub");

        Assert.Equal("MCPHub", vm.Model.HubDetailTitle);
        Assert.Equal("connected", vm.Model.HubState);
        Assert.Equal("mcphub", vm.Model.HubKey);
        Assert.Equal("17 tools", vm.Model.HubTools);
        Assert.Contains("management tools", vm.Model.HubManagement, StringComparison.Ordinal);

        var agents = vm.Model.HubAgents;
        output.WriteLine(string.Join(" | ", agents.Select(a => $"{a.Agent} ({a.UserId}): {a.Tools}")));

        Assert.Equal(2, agents.Count);
        Assert.Equal("dagger", agents[0].Agent);           // ordered, not however the wire arrived
        Assert.Equal("usr_dagger", agents[0].UserId);
        Assert.Equal("nothing granted there yet", agents[0].Tools);
        Assert.Equal("mcphub__gh_get_issue, mcphub__gh_list_issues", agents[1].Tools);
        Assert.Contains("key issued", agents[1].Issued, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", vm.Model.HubAgentsClass, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hub nobody is on is a hub an operator has yet to provision, not a load that failed - so
    /// the rows go rather than showing an empty box where names should be.
    /// </summary>
    [Fact]
    public void AHubWithNoAgentsHidesTheAgentRows()
    {
        var vm = Admin();
        vm.SetHubs([new HubPayload("empty", "Nobody", "Connected", "", 3, false, [])]);
        vm.SelectHub("empty");

        Assert.Empty(vm.Model.HubAgents);
        Assert.Contains("hidden", vm.Model.HubAgentsClass, StringComparison.Ordinal);

        // and the read-only case is stated, because everything else about such a hub looks fine.
        Assert.Contains("Read-only", vm.Model.HubManagement, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unreachable hub offers nothing to anyone, and its reason is the one thing on the page
    /// worth reading. It must not be mistakable for a hub that is simply empty.
    /// </summary>
    [Fact]
    public void AnUnreachableHubCarriesItsReason()
    {
        var vm = Admin();
        vm.SetHubs([new HubPayload("mcphub", "MCPHub", "Faulted", "no token presented", 0, false, [])]);
        vm.SelectHub("mcphub");

        Assert.Equal("faulted \u2014 no token presented", vm.Model.HubState);
        Assert.Contains("Not answering", vm.Model.HubDetailSubtitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The four ways a hub can be unconnected do not mean the same thing, and the page keeps them
    /// apart. "Unknown" especially: that is the broker's word for an upstream the registry has no
    /// entry for - this server never dialled it - and rendering it as "unknown" would read as a
    /// fault that has not happened.
    /// </summary>
    [Theory]
    [InlineData("Connected", "connected")]
    [InlineData("Connecting", "connecting")]
    [InlineData("Disconnected", "disconnected")]
    [InlineData("Faulted", "faulted")]
    [InlineData("Unknown", "not started - this server has not tried to connect")]
    public void EachStateIsSaidInWords(string wire, string expected)
    {
        var vm = Admin();
        vm.SetHubs([new HubPayload("h", "H", wire, "", 0, false, [])]);

        output.WriteLine($"{wire} -> {vm.Model.AdminHubs[0].State}");
        Assert.Equal(expected, vm.Model.AdminHubs[0].State);
    }

    /// <summary>
    /// "No hubs" and "I could not ask" look identical as an empty list, and only one of them is
    /// worth doing something about - so the refusal is put where the count would be.
    /// </summary>
    [Fact]
    public void AServerWithNoToolBackendSaysSo()
    {
        var vm = Admin();
        vm.SetHubs([Hubs.Sample]);
        vm.SelectHub("mcphub");

        vm.HubsUnavailable("This server has no tool backend.");

        Assert.Empty(vm.Model.AdminHubs);
        Assert.Equal("This server has no tool backend.", vm.Model.HubsStatus);
        Assert.Contains("hidden", vm.Model.HubDetailClass, StringComparison.Ordinal);
    }

    /// <summary>A hub that drops out of the listing takes its open pane with it, rather than
    /// leaving a detail pane describing something the server no longer has.</summary>
    [Fact]
    public void ARefreshThatLosesTheSelectedHubClosesThePane()
    {
        var vm = Admin();
        vm.SetHubs([Hubs.Sample]);
        vm.SelectHub("mcphub");
        Assert.DoesNotContain("hidden", vm.Model.HubDetailClass, StringComparison.Ordinal);

        vm.SetHubs([new HubPayload("other", "Other", "Connected", "", 2, true, [])]);

        Assert.Equal("", vm.Model.HubSelected);
        Assert.Contains("hidden", vm.Model.HubDetailClass, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rail is a place you are, not a set of things you have open. This is what the five
    /// openers used to get wrong in five different ways - agents closed users and nothing else,
    /// so opening it over work left both cards stacked and the top one was whichever had been
    /// clicked last.
    /// </summary>
    [Fact]
    public void OpeningAPageClosesEveryOtherPage()
    {
        var vm = Admin();
        vm.SetVoiceSettings("local", "en", "root", "", "", [], new Dictionary<string, string>());

        var openers = new (string Name, Action Open, Func<bool> IsOpen)[]
        {
            ("agents", () => vm.ShowAgentsPanel(true), () => vm.AgentsPanelOpen),
            ("users", () => vm.ShowUsersPanel(true), () => vm.UsersPanelOpen),
            ("work", () => vm.ShowWorkPanel(true), () => vm.WorkPanelOpen),
            ("hubs", () => vm.ShowHubsPanel(true), () => vm.HubsPanelOpen),
            ("settings", () => vm.ShowSettingsPanel(true), () => vm.SettingsPanelOpen),
        };

        foreach (var (name, open, _) in openers)
        {
            // Opened from every other page in turn, because what broke before was a PAIR: the
            // page being left, not the page being opened.
            foreach (var (fromName, from, _) in openers)
            {
                from();
                open();

                var stillOpen = openers.Where(o => o.IsOpen()).Select(o => o.Name).ToList();
                output.WriteLine($"{fromName} -> {name}: {string.Join(", ", stillOpen)}");
                Assert.Equal([name], stillOpen);
            }
        }
    }

    /// <summary>
    /// The grants reach the screen. Every view-model check above would pass with the field never
    /// rendered, which is how a page comes to show nothing while the tests are green.
    /// </summary>
    [Fact]
    public void TheGrantedNamesAreOnScreen()
    {
        var app = AppPages.Showing("hubs");
        using var doc = app.CreateDocument();
        doc.Refresh();
        doc.BuildFrame(1240, 800);

        var rows = Find(doc.Root, "hub-rows");
        Assert.NotNull(rows);

        var text = string.Join(" ", Texts(rows));
        output.WriteLine(text);

        Assert.Contains("scribe", text, StringComparison.Ordinal);
        Assert.Contains("usr_scribe", text, StringComparison.Ordinal);
        Assert.Contains("mcphub__gh_list_issues", text, StringComparison.Ordinal);
        Assert.True(rows.Width > 1 && rows.Height > 1, $"the rows box laid out {rows.Width}x{rows.Height}");
    }
}
