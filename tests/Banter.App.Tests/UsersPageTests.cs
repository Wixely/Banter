using Banter.App;
using Banter.Protocol;
using CupriFace;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The users tab of the admin page — the humans' mirror of <see cref="AgentsPageTests"/>.
///
/// <para>The one secret on this tab is a temporary password the server just invented, shown in
/// the same one-shot banner the enrolment code uses, and cleared the moment the tab changes or
/// the page closes. Nothing on this screen can display a password that is in use.</para>
/// </summary>
public sealed class UsersPageTests(ITestOutputHelper output)
{
    private const int Width = 1240;
    private const int Height = 800;

    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("root");
        vm.AddRoom("#main");
        vm.SetIsAdmin(true);
        return vm;
    }

    private static ChatViewModel OpenOnUsers()
    {
        var vm = Room();
        vm.ShowUsersPanel(true);
        return vm;
    }

    [Fact]
    public void TheUsersRailButtonOpensItsOwnPageAndAsksTheServer()
    {
        var vm = Room();
        var users = 0;
        var app = new BanterChatApp(vm) { UsersListAsync = () => { users++; return Task.CompletedTask; } };

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var (x, y) = PointOn(doc, "[data-users-open]");
        doc.DispatchClick(x, y, 1);

        Assert.Equal(1, users);
        Assert.True(vm.UsersPanelOpen);
        Assert.False(vm.AgentsPanelOpen);
    }

    [Fact]
    public void OpeningOnePageClosesTheOther()
    {
        var vm = Room();

        // Separate pages, not tabs: the rail is a place you are, so arriving at one leaves the
        // other — two admin pages stacked on each other is a state nobody asked for.
        vm.ShowAgentsPanel(true);
        Assert.True(vm.AgentsPanelOpen);

        vm.ShowUsersPanel(true);
        Assert.True(vm.UsersPanelOpen);
        Assert.False(vm.AgentsPanelOpen);

        vm.ShowAgentsPanel(true);
        Assert.False(vm.UsersPanelOpen);
    }

    [Fact]
    public void LeavingAPageTakesItsSecretWithIt()
    {
        var vm = OpenOnUsers();
        vm.ShowTempPassword("carol", "banter-temp-secret");
        Assert.DoesNotContain("hidden", vm.Model.AdminCodeClass, StringComparison.Ordinal);

        // A password shown on the users page is noise — or worse — on the agents page, and one
        // left behind after closing is a secret nobody is watching.
        vm.ShowAgentsPanel(true);
        Assert.Equal("", vm.Model.AdminCode);
        Assert.Contains("hidden", vm.Model.AdminCodeClass, StringComparison.Ordinal);
    }

    [Fact]
    public void BothRailButtonsAreAdminOnly()
    {
        var member = new ChatViewModel();
        member.SetNick("nell");
        member.SetIsAdmin(false);
        Assert.Contains("hidden", member.Model.AgentsButtonClass, StringComparison.Ordinal);
        Assert.Contains("hidden", member.Model.UsersButtonClass, StringComparison.Ordinal);

        var admin = Room();
        Assert.DoesNotContain("hidden", admin.Model.AgentsButtonClass, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", admin.Model.UsersButtonClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListShowsWhoIsAnAdminAtAGlance()
    {
        var vm = OpenOnUsers();
        vm.SetUsers([new UserAccountPayload("root", true), new UserAccountPayload("nell", false)]);

        output.WriteLine(string.Join(" | ", vm.Model.AdminUsers.Select(r => $"{r.Username}: {r.Detail}")));
        Assert.Equal("admin", vm.Model.AdminUsers[0].Detail);
        Assert.Equal("member", vm.Model.AdminUsers[1].Detail);
        Assert.Equal("2 users", vm.Model.AdminStatus);
    }

    [Fact]
    public void TheToggleButtonReadsAsTheActionItWouldTake()
    {
        var vm = OpenOnUsers();
        vm.SetUsers([new UserAccountPayload("root", true), new UserAccountPayload("nell", false)]);

        vm.SelectAdminUser("root");
        Assert.Equal("Make member", vm.Model.AdminUserToggleLabel);

        vm.SelectAdminUser("nell");
        Assert.Equal("Make admin", vm.Model.AdminUserToggleLabel);
    }

    [Fact]
    public void CreatingAUserSendsTheFormAndSelectionActsOnTheChosenRow()
    {
        var vm = Room();
        var created = new List<(string Name, bool IsAdmin)>();
        var removed = new List<string>();
        var app = new BanterChatApp(vm)
        {
            UserCreateAsync = (name, isAdmin) => { created.Add((name, isAdmin)); return Task.CompletedTask; },
            UserRemoveAsync = name => { removed.Add(name); return Task.CompletedTask; },
        };

        vm.ShowUsersPanel(true);
        vm.SetUsers([new UserAccountPayload("root", true), new UserAccountPayload("nell", false)]);

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        // The role toggle cycles member → admin, then the form goes to the server as typed.
        vm.Model.NewUserName = "carol";
        var (rx, ry) = PointOn(doc, ".admin-user-role");
        doc.DispatchClick(rx, ry, 1);
        doc.BuildDisplayList(Width, Height);
        var (ax, ay) = PointOn(doc, ".admin-user-add");
        doc.DispatchClick(ax, ay, 1);

        Assert.Equal(("carol", true), Assert.Single(created));

        // Remove without a selection is a no-op; with one, it names the selected row.
        doc.BuildDisplayList(Width, Height);
        var (dx, dy) = PointOn(doc, ".admin-user-remove");
        doc.DispatchClick(dx, dy, 1);
        Assert.Empty(removed);

        doc.BuildDisplayList(Width, Height);
        var (nx, ny) = PointOn(doc, "[data-admin-user=\"nell\"]");
        doc.DispatchClick(nx, ny, 1);
        doc.BuildDisplayList(Width, Height);
        var (dx2, dy2) = PointOn(doc, ".admin-user-remove");
        doc.DispatchClick(dx2, dy2, 1);

        Assert.Equal("nell", Assert.Single(removed));
    }

    /// <summary>Bounding-box centre of the first element matching <paramref name="selector"/>.</summary>
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
            throw new Xunit.Sdk.XunitException($"nothing painted matches {selector}");
        }

        return ((minX + maxX) / 2, (minY + maxY) / 2);
    }
}
