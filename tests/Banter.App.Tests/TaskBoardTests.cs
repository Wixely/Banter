using Banter.App;
using SkiaSharp;
using Xunit;

namespace Banter.App.Tests;

/// <summary>The work ledger made visible in the app (PLAN §8b).</summary>
public sealed class TaskBoardTests
{
    private static ChatViewModel Room(string room = "#main")
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom(room);
        return vm;
    }

    [Fact]
    public void TheBoardIsHiddenUntilThereIsWork()
    {
        var vm = Room();

        Assert.Equal("tasks hidden", vm.Model.TasksClass);

        vm.SetTask("#main", "t1", "fix the parser", "Open", null);

        Assert.Equal("tasks", vm.Model.TasksClass);
    }

    [Fact]
    public void OpenAndHeldWorkReadDifferently()
    {
        var vm = Room();

        vm.SetTask("#main", "t1", "unclaimed", "Open", null);
        vm.SetTask("#main", "t2", "in progress", "Claimed", "dagger");

        var open = vm.Model.Tasks.Single(t => t.TaskId == "t1");
        var held = vm.Model.Tasks.Single(t => t.TaskId == "t2");

        Assert.Equal("open", open.Status);
        Assert.Equal("task", open.RowClass);
        Assert.Equal("claimed · dagger", held.Status);
        Assert.Equal("task held", held.RowClass);
    }

    [Fact]
    public void AnUpdateChangesTheExistingRowRatherThanAddingOne()
    {
        var vm = Room();
        vm.SetTask("#main", "t1", "fix the parser", "Open", null);

        vm.SetTask("#main", "t1", "fix the parser", "Claimed", "dagger");

        var row = Assert.Single(vm.Model.Tasks);
        Assert.Equal("claimed · dagger", row.Status);
    }

    [Fact]
    public void FinishedWorkLeavesTheBoard()
    {
        var vm = Room();
        vm.SetTask("#main", "t1", "fix the parser", "Claimed", "dagger");

        // The panel answers "what is happening now"; the timeline records what happened.
        vm.SetTask("#main", "t1", "fix the parser", "Done", "dagger");

        Assert.Empty(vm.Model.Tasks);
        Assert.Equal("tasks hidden", vm.Model.TasksClass);
    }

    [Fact]
    public void FailedWorkAlsoLeavesTheBoard()
    {
        var vm = Room();
        vm.SetTask("#main", "t1", "fix the parser", "Claimed", "dagger");

        vm.SetTask("#main", "t1", "fix the parser", "Failed", "dagger");

        Assert.Empty(vm.Model.Tasks);
    }

    [Fact]
    public void TheBoardFollowsTheActiveRoom()
    {
        var vm = Room();
        vm.AddRoom("#other");
        vm.SetTask("#main", "t1", "main work", "Open", null);
        vm.SetTask("#other", "t2", "other work", "Open", null);

        Assert.Equal("main work", Assert.Single(vm.Model.Tasks).Title);

        vm.SwitchTo("#other");
        Assert.Equal("other work", Assert.Single(vm.Model.Tasks).Title);
    }

    [Fact]
    public void ReplacingTheBoardDropsStaleWork()
    {
        var vm = Room();
        vm.SetTask("#main", "t1", "old", "Open", null);

        vm.SetTasks("#main", [("t2", "new", "Open", null)]);

        Assert.Equal("new", Assert.Single(vm.Model.Tasks).Title);
    }

    [Theory]
    [InlineData("fix the parser", "fix the parser", "")]
    [InlineData("fix the parser -- it throws on empty input", "fix the parser", "it throws on empty input")]
    [InlineData("  padded  --  and detail  ", "padded", "and detail")]
    public void TaskTextSplitsOnTheDetailMarker(string input, string title, string body)
    {
        var (actualTitle, actualBody) = BanterChatSession.SplitTitleAndBody(input);

        Assert.Equal(title, actualTitle);
        Assert.Equal(body, actualBody);
    }

    [Fact]
    public void TheAppRendersWithABoardPresent()
    {
        var vm = Room();
        vm.SetAgents("#main", [("dagger", true, "code", true)]);
        vm.SetTask("#main", "t1", "fix the parser", "Claimed", "dagger");
        vm.SetTask("#main", "t2", "write the docs", "Open", null);
        vm.Append("#main", "alice", "hello", 0);

        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);
        var pixels = doc.RenderToPixels(1100, 760, SKColors.Black);

        Assert.Contains(pixels, b => b != 0);
    }
}
