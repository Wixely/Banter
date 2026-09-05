using Banter.App;
using Banter.Protocol;
using CupriFace;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The agents and users pages, which are one page emitted twice from one template.
///
/// <para>Most of these are theories run against BOTH pages. That is the point: the two used to be
/// separate markup kept in step by hand and had already drifted, so a test that only exercises one
/// of them is a test that would not have caught the drift it exists to prevent.</para>
///
/// <para>The other half is the create/edit distinction. The page used to show an "add" form and an
/// "act on the selection" column at once, so what a button would do depended on which half you had
/// last touched; every assertion about titles, save labels and the remove button is about that
/// never being ambiguous again.</para>
/// </summary>
public sealed class ManagementPageTests(ITestOutputHelper output)
{
    private const int Width = 1240;
    private const int Height = 800;

    private static AgentIdentityPayload Identity(
        string nick, bool enrolled = true, string locality = "local",
        int? cost = null, bool? wants = null) =>
        new(nick, ["#main"], ["chat"], locality, "sensitive", enrolled,
            enrolled ? "3f2a 91c0 be47 1d08" : "", !enrolled, cost, wants);

    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("root");
        vm.AddRoom("#main");
        vm.SetIsAdmin(true);
        vm.SetAgentIdentities([Identity("dagger"), Identity("scout", locality: "frontier")]);
        vm.SetUsers([new UserAccountPayload("root", true), new UserAccountPayload("nell", false)]);
        return vm;
    }

    /// <summary>The two pages, as the things a theory needs to drive either one.</summary>
    public static TheoryData<string> BothPages() => new() { "agents", "users" };

    private static void Open(ChatViewModel vm, string page)
    {
        if (page == "agents")
        {
            vm.ShowAgentsPanel(true);
        }
        else
        {
            vm.ShowUsersPanel(true);
        }
    }

    private static void New(ChatViewModel vm, string page)
    {
        if (page == "agents")
        {
            vm.NewAgent();
        }
        else
        {
            vm.NewUser();
        }
    }

    private static void Select(ChatViewModel vm, string page, string name)
    {
        if (page == "agents")
        {
            vm.SelectAdminAgent(name);
        }
        else
        {
            vm.SelectAdminUser(name);
        }
    }

    private static string Existing(string page) => page == "agents" ? "dagger" : "nell";

    private static (string Title, string Save, string Remove, string Detail, string Empty, string Dirty)
        Pane(ChatViewModel vm, string page) => page == "agents"
            ? (vm.Model.AgentDetailTitle, vm.Model.AgentSaveLabel, vm.Model.AgentRemoveClass,
               vm.Model.AgentDetailClass, vm.Model.AgentEmptyClass, vm.Model.AgentDirtyClass)
            : (vm.Model.UserDetailTitle, vm.Model.UserSaveLabel, vm.Model.UserRemoveClass,
               vm.Model.UserDetailClass, vm.Model.UserEmptyClass, vm.Model.UserDirtyClass);

    // ── The create/edit distinction ──────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BothPages))]
    public void APageOpensWithNothingChosenAndSaysSo(string page)
    {
        var vm = Room();
        Open(vm, page);

        // No form at all rather than an empty one: an empty form is indistinguishable from one
        // that is creating something, which is the confusion this whole page was rebuilt over.
        var pane = Pane(vm, page);
        Assert.Contains("hidden", pane.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", pane.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothPages))]
    public void CreatingAndEditingCannotBeMistakenForEachOther(string page)
    {
        var vm = Room();
        Open(vm, page);

        New(vm, page);
        var creating = Pane(vm, page);
        output.WriteLine($"new: '{creating.Title}' / '{creating.Save}'");

        // Creating: says "new", offers to create, and has nothing to remove yet.
        Assert.Contains("New", creating.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Create", creating.Save, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hidden", creating.Remove, StringComparison.Ordinal);

        Select(vm, page, Existing(page));
        var editing = Pane(vm, page);
        output.WriteLine($"edit: '{editing.Title}' / '{editing.Save}'");

        // Editing: titled with the subject, saves rather than creates, and can be removed.
        Assert.Equal(Existing(page), editing.Title);
        Assert.Contains("Save", editing.Save, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden", editing.Remove, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothPages))]
    public void StartingSomethingNewDropsTheSelection(string page)
    {
        var vm = Room();
        Open(vm, page);
        Select(vm, page, Existing(page));

        New(vm, page);

        // Otherwise the list would still point at a row the form is no longer about, and Remove
        // would act on it.
        var selected = page == "agents" ? vm.Model.AgentSelected : vm.Model.UserSelected;
        Assert.Equal("", selected);
        var rows = page == "agents"
            ? vm.Model.AdminAgents.Select(r => r.RowClass)
            : vm.Model.AdminUsers.Select(r => r.RowClass);
        Assert.All(rows, c => Assert.DoesNotContain("selected", c, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(BothPages))]
    public void EditingSaysNothingIsUnsavedUntilSomethingChanges(string page)
    {
        var vm = Room();
        Open(vm, page);

        Select(vm, page, Existing(page));
        Assert.Contains("hidden", Pane(vm, page).Dirty, StringComparison.Ordinal);

        if (page == "agents")
        {
            vm.Model.AgentFormSkills = "chat, notes";
            vm.RefreshAgentDirty();
        }
        else
        {
            vm.ChooseUserRole("admin");
        }

        Assert.DoesNotContain("hidden", Pane(vm, page).Dirty, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothPages))]
    public void CreatingIsAlwaysUnsaved(string page)
    {
        var vm = Room();
        Open(vm, page);
        New(vm, page);

        // Nothing has been written yet by definition, so the footer says so from the start.
        Assert.DoesNotContain("hidden", Pane(vm, page).Dirty, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothPages))]
    public void ReopeningAPageForgetsWhatWasOnIt(string page)
    {
        var vm = Room();
        Open(vm, page);
        New(vm, page);

        Open(vm, page);

        // A half-typed form is not worth restoring, and a secret left on it is worse.
        Assert.Contains("hidden", Pane(vm, page).Detail, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothPages))]
    public void ARefreshThatRemovesTheSubjectClosesTheForm(string page)
    {
        var vm = Room();
        Open(vm, page);
        Select(vm, page, Existing(page));
        Assert.DoesNotContain("hidden", Pane(vm, page).Detail, StringComparison.Ordinal);

        // Somebody else deleted it while this page was open. Editing a thing that is gone would
        // save into nothing and report a confusing refusal.
        if (page == "agents")
        {
            vm.SetAgentIdentities([Identity("scout", locality: "frontier")]);
        }
        else
        {
            vm.SetUsers([new UserAccountPayload("root", true)]);
        }

        Assert.Contains("hidden", Pane(vm, page).Detail, StringComparison.Ordinal);
    }

    // ── Clicking away ────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BothPages))]
    public void ClickingOutsideTheCardClosesThePageAndClickingInsideDoesNot(string page)
    {
        var vm = Room();
        var app = new BanterChatApp(vm);
        Open(vm, page);

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        // Inside first: a click on the card must not close it, or nothing on the page would be
        // usable. The list heading is a point that is certainly inside and not a control.
        var (ix, iy) = PointOn(doc, ".mgmt-title");
        doc.DispatchClick(ix, iy, 1);
        Assert.True(page == "agents" ? vm.AgentsPanelOpen : vm.UsersPanelOpen);

        doc.BuildDisplayList(Width, Height);
        var (bx, by) = FirstPointOn(doc, ".mgmt-backdrop");
        output.WriteLine($"backdrop hit at {bx},{by}");
        doc.DispatchClick(bx, by, 1);

        Assert.False(page == "agents" ? vm.AgentsPanelOpen : vm.UsersPanelOpen);
    }

    // ── Shared shape ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BothPages))]
    public void EveryPartOfThePageIsPaintedOnBothPages(string page)
    {
        var vm = Room();
        var app = new BanterChatApp(vm);
        Open(vm, page);
        Select(vm, page, Existing(page));

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        // One template means one set of parts. If a class paints on one page and not the other,
        // the template has grown a branch and the pages have started to diverge again.
        foreach (var selector in new[]
        {
            ".mgmt-backdrop", ".mgmt-card", ".mgmt-list", ".mgmt-new", ".mgmt-rows", ".mgmt-row",
            ".mgmt-pane", ".mgmt-detail", ".mgmt-remove", ".mgmt-fields", ".mgmt-label",
            ".mgmt-choice", ".mgmt-dot", ".mgmt-footer",
        })
        {
            Assert.True(Paints(doc, selector), $"{selector} painted nothing on the {page} page");
        }
    }

    [Fact]
    public void OneTemplateEmitsBothPages()
    {
        // The structural claim behind every theory above: whatever differs between the two pages
        // is a binding, not a shape. Comparing the emitted markup with the bindings stripped is
        // what makes "they cannot drift" a checked statement rather than an intention.
        var html = new BanterChatApp(Room()).Html;
        var agents = Section(html, "{{AgentsPanelClass}}");
        var users = Section(html, "{{UsersPanelClass}}");

        Assert.Equal(Skeleton(agents), Skeleton(users));
    }

    /// <summary>One page's markup, from its panel class to the end of its card.</summary>
    private static string Section(string html, string panelBinding)
    {
        var start = html.IndexOf(panelBinding, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{panelBinding} is not in the markup");
        var end = html.IndexOf("mgmt-backdrop", start + panelBinding.Length, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }

    /// <summary>
    /// The markup with everything page-specific removed: bindings, repeat targets, data- actions,
    /// class names and text. What survives is the tag structure, which must match exactly.
    /// </summary>
    private static string Skeleton(string markup)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(markup, "{{[^}]*}}", "");
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, "\"[^\"]*\"", "\"\"");
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, ">[^<]*<", "><");
        return System.Text.RegularExpressions.Regex.Replace(stripped, @"\s+", " ").Trim();
    }

    private static bool Paints(CupriDocument doc, string selector)
    {
        for (var y = 0f; y < Height; y += 3)
        {
            for (var x = 0f; x < Width; x += 3)
            {
                if (doc.HitTest(x, y)?.Element?.Closest(selector) is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The FIRST point that hits <paramref name="selector"/>, not the centre of everything that
    /// does. The backdrop covers the whole window, so the centre of its painted extent is the
    /// middle of the screen — which is where the card is, and where a click deliberately does
    /// not reach it.
    /// </summary>
    private static (float X, float Y) FirstPointOn(CupriDocument doc, string selector)
    {
        for (var y = 0f; y < Height; y += 2)
        {
            for (var x = 0f; x < Width; x += 2)
            {
                if (doc.HitTest(x, y)?.Element?.Closest(selector) is not null)
                {
                    return (x, y);
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"nothing painted matches {selector}");
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
