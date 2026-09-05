using Banter.App;
using Banter.Protocol;
using CupriFace;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The agents page — creating, editing and removing agents from the desktop client rather than
/// from a config file.
///
/// <para>No private key passes through this screen. It mints a one-time enrolment code and the
/// agent's own machine makes the key, so the code is the only secret here — which is why it is
/// shown once and taken off screen when the page closes.</para>
/// </summary>
public sealed class AgentsPageTests(ITestOutputHelper output)
{
    private const int Width = 1240;
    private const int Height = 800;

    private static AgentIdentityPayload Identity(
        string nick, bool enrolled = true, bool pending = false, string locality = "local") =>
        new(nick, ["#main"], ["chat"], locality, "sensitive", enrolled,
            enrolled ? "3f2a 91c0 be47 1d08" : "", pending);

    private static ChatViewModel Room(bool admin = true)
    {
        var vm = new ChatViewModel();
        vm.SetNick("root");
        vm.AddRoom("#main");
        vm.SetIsAdmin(admin);
        return vm;
    }

    [Fact]
    public void SelectingAnAgentShowsItsStandingOverrides()
    {
        var vm = Room();
        vm.SetAgentIdentities([
            Identity("dagger"),
            new AgentIdentityPayload("scout", ["#main"], ["web"], "frontier", "public",
                true, "3f2a 91c0 be47 1d08", false, CostTier: 7, WantsDelegator: false),
        ]);

        // No overrides: the panel says so rather than showing zeros that look chosen.
        vm.SelectAdminAgent("dagger");
        Assert.Equal("", vm.Model.AdminCostOverride);
        Assert.Equal("Delegator: agent decides", vm.Model.AdminDelegatorLabel);

        // Overrides: shown as the absolute state Apply would write back, and worn on the row.
        vm.SelectAdminAgent("scout");
        Assert.Equal("7", vm.Model.AdminCostOverride);
        Assert.Equal("Delegator: never", vm.Model.AdminDelegatorLabel);
        Assert.Contains("cost 7", vm.Model.AdminAgents.Single(a => a.Nick == "scout").Detail, StringComparison.Ordinal);
        Assert.Contains("delegator never", vm.Model.AdminAgents.Single(a => a.Nick == "scout").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverridePanelReadsBackExactlyWhatItShows()
    {
        var vm = Room();
        vm.SetAgentIdentities([Identity("dagger")]);
        vm.SelectAdminAgent("dagger");

        // agent decides → pinned → never → agent decides again.
        vm.CycleAdminDelegator();
        vm.Model.AdminCostOverride = "3";
        Assert.Equal((3, true), vm.ReadAgentOverrides());

        vm.CycleAdminDelegator();
        vm.Model.AdminCostOverride = "";
        Assert.Equal(((int?)null, (bool?)false), vm.ReadAgentOverrides());

        vm.CycleAdminDelegator();
        Assert.Equal(((int?)null, (bool?)null), vm.ReadAgentOverrides());

        // A cost that is not a number never becomes a request.
        vm.Model.AdminCostOverride = "cheap";
        Assert.Null(vm.ReadAgentOverrides());
        Assert.Contains("number", vm.Model.AdminStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplySendsTheOverridesForTheSelectedAgent()
    {
        var vm = Room();
        var applied = new List<(string Nick, int? Cost, bool? Wants)>();
        var app = new BanterChatApp(vm)
        {
            AgentOverridesAsync = (nick, cost, wants) => { applied.Add((nick, cost, wants)); return Task.CompletedTask; },
        };

        vm.ShowAdminPanel(true);
        vm.SetAgentIdentities([Identity("dagger"), Identity("scribe")]);
        vm.SelectAdminAgent("scribe");
        vm.Model.AdminCostOverride = "4";
        vm.CycleAdminDelegator();                       // agent decides → pinned

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);
        var (x, y) = PointOn(doc, ".admin-apply");
        doc.DispatchClick(x, y, 1);

        Assert.Equal(("scribe", 4, true), Assert.Single(applied));
    }

    [Fact]
    public void TheRailButtonIsOnlyThereForAnAdmin()
    {
        // The server refuses everyone else, so offering the button would be offering a refusal.
        Assert.Contains("hidden", Room(admin: false).Model.AdminButtonClass, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", Room(admin: true).Model.AdminButtonClass, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningThePageAsksTheServerForTheAgents()
    {
        var vm = Room();
        var listed = 0;
        var app = new BanterChatApp(vm) { AgentsListAsync = () => { listed++; return Task.CompletedTask; } };

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var (x, y) = PointOn(doc, "[data-admin-open]");
        doc.DispatchClick(x, y, 1);

        Assert.True(vm.AdminPanelOpen);
        Assert.Equal(1, listed);
    }

    [Fact]
    public void TheListShowsWhetherEachAgentCanActuallyConnect()
    {
        var vm = Room();
        vm.SetAgentIdentities([
            Identity("dagger"),
            Identity("scribe", enrolled: false, pending: true),
            Identity("ghost", enrolled: false),
        ]);

        var rows = vm.Model.AdminAgents;
        output.WriteLine(string.Join(" | ", rows.Select(r => $"{r.Nick}: {r.State}")));

        // An enrolled agent shows which machine holds it; the others say what is missing, because
        // an identity with no key cannot connect and that is the thing worth noticing.
        Assert.Equal("3f2a 91c0 be47 1d08", rows[0].State);
        Assert.DoesNotContain("pending", rows[0].StateClass, StringComparison.Ordinal);

        Assert.Contains("waiting", rows[1].State, StringComparison.Ordinal);
        Assert.Contains("pending", rows[1].StateClass, StringComparison.Ordinal);

        Assert.Contains("reissue", rows[2].State, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatingAnAgentSendsWhatTheFormSays()
    {
        var vm = Room();
        (string Nick, IReadOnlyList<string> Rooms, IReadOnlyList<string> Skills, AgentLocality Locality, DataSensitivity Clearance)? sent = null;

        var app = new BanterChatApp(vm)
        {
            AgentCreateAsync = (nick, rooms, skills, locality, clearance) =>
            {
                sent = (nick, rooms, skills, locality, clearance);
                return Task.CompletedTask;
            },
        };

        vm.ShowAdminPanel(true);
        vm.Model.NewAgentNick = "scribe";
        vm.Model.NewAgentRooms = "#notes, #main";
        vm.Model.NewAgentSkills = "notes, minutes";
        vm.CycleNewAgentLocality();                 // local -> frontier
        vm.CycleNewAgentClearance();                // sensitive -> public

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);
        var (x, y) = PointOn(doc, ".admin-add");
        doc.DispatchClick(x, y, 1);

        Assert.NotNull(sent);
        Assert.Equal("scribe", sent!.Value.Nick);
        Assert.Equal(["#notes", "#main"], sent.Value.Rooms);
        Assert.Equal(["notes", "minutes"], sent.Value.Skills);
        Assert.Equal(AgentLocality.Frontier, sent.Value.Locality);
        Assert.Equal(DataSensitivity.Public, sent.Value.Clearance);
    }

    [Fact]
    public void AnAgentWithNoNameIsNotCreated()
    {
        var vm = Room();
        var calls = 0;
        var app = new BanterChatApp(vm)
        {
            AgentCreateAsync = (_, _, _, _, _) => { calls++; return Task.CompletedTask; },
        };

        vm.ShowAdminPanel(true);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);
        var (x, y) = PointOn(doc, ".admin-add");
        doc.DispatchClick(x, y, 1);

        Assert.Equal(0, calls);
        Assert.Contains("name", vm.Model.AdminStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClosingThePageTakesTheCodeOffTheScreen()
    {
        var vm = Room();
        vm.ShowAdminPanel(true);
        vm.ShowEnrolmentCode("scribe", "banter-enrol-secret");
        Assert.DoesNotContain("hidden", vm.Model.AdminCodeClass, StringComparison.Ordinal);

        vm.ShowAdminPanel(false);

        // The code is the one secret on this screen and the server keeps only a hash of it, so
        // leaving it up after the page closes would be leaving a credential nobody is watching.
        Assert.Equal("", vm.Model.AdminCode);
        Assert.Contains("hidden", vm.Model.AdminCodeClass, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveAndReissueActOnWhicheverAgentIsSelected()
    {
        var vm = Room();
        var removed = "";
        var reissued = "";
        var app = new BanterChatApp(vm)
        {
            AgentRemoveAsync = nick => { removed = nick; return Task.CompletedTask; },
            AgentReissueAsync = nick => { reissued = nick; return Task.CompletedTask; },
        };

        vm.ShowAdminPanel(true);
        vm.SetAgentIdentities([Identity("dagger"), Identity("scribe")]);
        vm.SelectAdminAgent("scribe");

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var (rx, ry) = PointOn(doc, ".admin-reissue");
        doc.DispatchClick(rx, ry, 1);
        Assert.Equal("scribe", reissued);

        // Repaint between the two. A running app paints every frame; a headless document only
        // when asked, and a click routed against a stale display list reaches nothing.
        doc.BuildDisplayList(Width, Height);

        var (dx, dy) = PointOn(doc, ".admin-remove");
        doc.DispatchClick(dx, dy, 1);
        Assert.Equal("scribe", removed);
    }

    [Fact]
    public void WithNothingSelectedNeitherButtonDoesAnything()
    {
        var vm = Room();
        var calls = 0;
        var app = new BanterChatApp(vm)
        {
            AgentRemoveAsync = _ => { calls++; return Task.CompletedTask; },
            AgentReissueAsync = _ => { calls++; return Task.CompletedTask; },
        };

        vm.ShowAdminPanel(true);
        vm.SetAgentIdentities([Identity("dagger")]);

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        // Nothing picked, so "remove this agent" has no subject — better silent than removing
        // whichever happened to be first.
        var (dx, dy) = PointOn(doc, ".admin-remove");
        doc.DispatchClick(dx, dy, 1);

        doc.BuildDisplayList(Width, Height);
        var (rx, ry) = PointOn(doc, ".admin-reissue");
        doc.DispatchClick(rx, ry, 1);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void ClickingAnAgentSelectsIt()
    {
        var vm = Room();
        var app = new BanterChatApp(vm);
        vm.ShowAdminPanel(true);
        vm.SetAgentIdentities([Identity("dagger"), Identity("scribe")]);

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var (x, y) = PointOn(doc, "[data-admin-agent=\"scribe\"]");
        doc.DispatchClick(x, y, 1);

        Assert.Equal("scribe", vm.Model.AdminSelected);
        Assert.Contains("selected", vm.Model.AdminAgents.Single(a => a.Nick == "scribe").RowClass, StringComparison.Ordinal);
        Assert.DoesNotContain("selected", vm.Model.AdminAgents.Single(a => a.Nick == "dagger").RowClass, StringComparison.Ordinal);
    }

    /// <summary>
    /// The middle of the element, found by asking what is painted where.
    ///
    /// <para>The middle rather than the first pixel that hit-tests: the first is the top-left
    /// corner, which sits on the border, and a click there is not reliably a click on the control.
    /// Scanning finds the extent, then the centre of it is aimed at.</para>
    /// </summary>
    private static (float X, float Y) PointOn(CupriDocument doc, string selector)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = -1, maxY = -1;

        for (var y = 0f; y < Height; y += 2)
        {
            for (var x = 0f; x < Width; x += 4)
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
            throw new Xunit.Sdk.XunitException($"nothing painted matches '{selector}'");
        }

        return ((minX + maxX) / 2, (minY + maxY) / 2);
    }
}
