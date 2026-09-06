using Banter.App;
using Banter.Protocol;
using CupriFace;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The work page: every room's tasks, for an operator rather than a participant.
///
/// <para>The roster's Work strip stays what it is — this room, title and state, glanceable while
/// you talk. This page answers the other question: across the whole server, what is stuck, who has
/// it, and is the lease about to hand it to somebody else.</para>
/// </summary>
public sealed class WorkPageTests(ITestOutputHelper output)
{
    private const int Width = 1240;
    private const int Height = 800;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static TaskInfoPayload Job(
        string id, string room = "#main", TaskState state = TaskState.Open,
        string? assignee = null, long? lease = null, string? result = null) =>
        new(id, room, $"task {id}", "the body", "alice", state, assignee,
            Now - 60_000, assignee is null ? null : Now - 30_000,
            state is TaskState.Done or TaskState.Failed ? Now : null, lease, result);

    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("root");
        vm.AddRoom("#main");
        vm.SetIsAdmin(true);
        return vm;
    }

    [Fact]
    public void TheRailButtonIsAdminOnly()
    {
        var member = new ChatViewModel();
        member.SetNick("nell");
        member.SetIsAdmin(false);
        Assert.Contains("hidden", member.Model.WorkButtonClass, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", Room().Model.WorkButtonClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListSaysWhereEachTaskIsAndWhoHasIt()
    {
        var vm = Room();
        vm.SetTasks([
            Job("a", state: TaskState.Open),
            Job("b", room: "#notes", state: TaskState.Claimed, assignee: "scribe"),
        ]);

        var rows = vm.Model.AdminTasks;
        output.WriteLine(string.Join(" | ", rows.Select(r => $"{r.Title}: {r.Detail} / {r.State}")));

        // Unclaimed work is the thing worth spotting, so it is the one that is marked.
        Assert.Contains("#main", rows[0].Detail, StringComparison.Ordinal);
        Assert.Contains("pending", rows[0].StateClass, StringComparison.Ordinal);
        Assert.Equal("waiting for an agent", rows[0].State);

        Assert.Contains("scribe", rows[1].Detail, StringComparison.Ordinal);
        Assert.Contains("#notes", rows[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingATaskShowsEverythingAboutIt()
    {
        var vm = Room();
        vm.SetTasks([Job("a", state: TaskState.Claimed, assignee: "scribe", lease: Now + 120_000)]);

        vm.SelectTask("a");

        Assert.Equal("task a", vm.Model.TaskDetailTitle);
        Assert.Equal("scribe", vm.Model.TaskAssignee);
        Assert.Equal("#main", vm.Model.TaskRoom);
        Assert.Equal("alice", vm.Model.TaskPoster);
        Assert.Equal("a", vm.Model.TaskId);
        Assert.Contains("posted", vm.Model.TaskTimes, StringComparison.Ordinal);
        Assert.Contains("held for another", vm.Model.TaskLease, StringComparison.Ordinal);
    }

    [Fact]
    public void ALapsedLeaseSaysTheWorkIsAboutToBeHandedBack()
    {
        var vm = Room();
        vm.SetTasks([Job("a", state: TaskState.Claimed, assignee: "scribe", lease: Now - 1_000)]);
        vm.SelectTask("a");

        // The whole reason the lease has a line of its own: an operator watching a task stall
        // wants to see the hand-back coming rather than discover it afterwards.
        output.WriteLine(vm.Model.TaskLease);
        Assert.Contains("expired", vm.Model.TaskLease, StringComparison.Ordinal);
    }

    [Fact]
    public void AResultOnlyTakesUpRoomWhenThereIsOne()
    {
        var vm = Room();
        vm.SetTasks([Job("a"), Job("b", state: TaskState.Done, assignee: "scribe", result: "wrote it up")]);

        vm.SelectTask("a");
        Assert.Contains("hidden", vm.Model.TaskResultClass, StringComparison.Ordinal);

        vm.SelectTask("b");
        Assert.DoesNotContain("hidden", vm.Model.TaskResultClass, StringComparison.Ordinal);
        Assert.Equal("wrote it up", vm.Model.TaskResult);
    }

    [Fact]
    public void FinishedWorkIsHiddenUntilAskedFor()
    {
        var vm = Room();
        Assert.False(vm.IncludeFinished);

        vm.ChooseTaskScope("all");
        Assert.True(vm.IncludeFinished);

        vm.ChooseTaskScope("live");
        Assert.False(vm.IncludeFinished);
    }

    [Fact]
    public void ARefreshThatLosesTheSelectedTaskClosesTheDetail()
    {
        var vm = Room();
        vm.SetTasks([Job("a"), Job("b")]);
        vm.SelectTask("a");
        Assert.DoesNotContain("hidden", vm.Model.TaskDetailClass, StringComparison.Ordinal);

        // Somebody finished it and the scope hides finished work: a pane about a task that is no
        // longer listed is showing state nothing can refresh.
        vm.SetTasks([Job("b")]);
        Assert.Contains("hidden", vm.Model.TaskDetailClass, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningWorkLeavesTheOtherPages()
    {
        var vm = Room();
        vm.ShowAgentsPanel(true);
        vm.ShowWorkPanel(true);

        Assert.True(vm.WorkPanelOpen);
        Assert.False(vm.AgentsPanelOpen);
    }

    [Fact]
    public void TheRailButtonOpensItAndAsksTheServer()
    {
        var vm = Room();
        var loads = 0;
        var app = new BanterChatApp(vm)
        {
            WorkListAsync = () => { loads++; return System.Threading.Tasks.Task.CompletedTask; },
        };

        using var doc = app.CreateDocument();
        doc.Refresh();
        doc.BuildDisplayList(Width, Height);

        var (x, y) = PointOn(doc, "[data-work-open]");
        doc.DispatchClick(x, y, 1);

        Assert.True(vm.WorkPanelOpen);
        Assert.Equal(1, loads);
    }

    [Fact]
    public void ThePageIsReadOnly()
    {
        var vm = Room();
        vm.ShowWorkPanel(true);
        vm.SetTasks([Job("a")]);
        vm.SelectTask("a");

        using var doc = new BanterChatApp(vm).CreateDocument();
        doc.Refresh();
        doc.BuildDisplayList(Width, Height);

        // Handing work out is the delegator's job; a page that let an admin claim on an agent's
        // behalf would be a second way to decide the same thing.
        Assert.False(Paints(doc, ".mgmt-save-task"));
        Assert.False(Paints(doc, ".mgmt-remove"));
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
