using Banter.App;
using CupriFace.Interaction;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The real <see cref="BanterChatApp"/> driven through a real document, headlessly. These are the
/// tests that would catch broken markup, a binding path that no longer resolves, or a handler
/// wired to a selector that does not exist.
/// </summary>
public sealed class BanterChatAppTests(ITestOutputHelper output)
{
    private const int Width = 1100;
    private const int Height = 760;

    private static (BanterChatApp App, ChatViewModel Vm, List<(string Room, string Text)> Sent) Build()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        var sent = new List<(string, string)>();
        var app = new BanterChatApp(vm)
        {
            SendAsync = (room, text) => { sent.Add((room, text)); return Task.CompletedTask; },
        };
        return (app, vm, sent);
    }

    [Fact]
    public void AppLaysOutAndPaintsWithMessagesPresent()
    {
        var (app, vm, _) = Build();
        vm.Append("#main", "bob", "hello there", 0);
        vm.Append("#main", "dagger", string.Join(' ', Enumerable.Repeat("wrapping text", 40)), 0);

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);
        var pixels = doc.RenderToPixels(Width, Height, SKColors.Black);

        Assert.Equal(Width * Height * 4, pixels.Length);
        Assert.Contains(pixels, b => b != 0);
    }

    /// <summary>
    /// Fastest of several layouts of a room holding <paramref name="messages"/>. The minimum, not
    /// the mean: scheduler noise only ever adds time, so the best sample is the closest thing to
    /// the cost of the work itself on a machine running six test assemblies at once.
    /// </summary>
    private static double LayoutMilliseconds(int messages)
    {
        var (app, vm, _) = Build();
        for (var i = 0; i < messages; i++)
        {
            // Variable-height rows: the case the timeline is actually built for.
            vm.Append("#main", i % 2 == 0 ? "bob" : "dagger",
                string.Join(' ', Enumerable.Repeat("message text", 3 + (i % 6) * 15)), 0);
        }

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);                // warm up, then measure

        var best = double.MaxValue;
        for (var i = 0; i < 10; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            doc.BuildDisplayList(Width, Height);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    [Fact]
    public void TimelineCostStaysFlatAsTheRoomFillsUp()
    {
        var small = LayoutMilliseconds(100);
        var large = LayoutMilliseconds(5_000);
        var growth = large / Math.Max(small, 0.001);

        output.WriteLine($"100 messages: {small:F3} ms, 5,000 messages: {large:F3} ms (x{growth:F1})");

        // A ratio rather than a millisecond budget. What this test is for is catching the loss of
        // virtualization, and that shows up as cost scaling with the room — fifty times the
        // messages measured fifty times the work. A wall-clock threshold cannot tell that apart
        // from a loaded runner, which is what made this fail whenever the whole solution ran.
        Assert.True(growth < 8, $"Layout cost grew x{growth:F1} between a 100- and 5,000-message room.");
    }

    [Fact]
    public void ClickingSendDeliversTheComposerToTheActiveRoomAndClearsIt()
    {
        var (app, vm, sent) = Build();
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        vm.Model.Composer = "hello from the app";
        app.Send();

        Assert.Equal(("#main", "hello from the app"), Assert.Single(sent));
        Assert.Equal("", vm.Model.Composer);
    }

    [Fact]
    public void SendIsIgnoredWhenThereIsNothingToSay()
    {
        var (app, _, sent) = Build();
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        app.Send();                                   // empty
        app.ViewModel.Model.Composer = "   ";
        app.Send();                                   // whitespace only

        Assert.Empty(sent);
    }

    [Fact]
    public void PresentDrainsQueuedNetworkUpdatesOntoTheRenderThread()
    {
        var (app, vm, _) = Build();
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        // Exactly what BanterChatSession does from the client's receive loop.
        vm.Post(() => vm.Append("#main", "bob", "arrived while idle", 0));
        Assert.Empty(vm.Model.Messages);

        app.Present(Width, Height);

        Assert.Equal("arrived while idle", Assert.Single(vm.Model.Messages).Text);
    }

    [Fact]
    public void LoadOlderAsksTheHostOnlyWhenThereIsMoreToFetch()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        var asked = new List<string>();
        var app = new BanterChatApp(vm) { LoadOlderAsync = r => { asked.Add(r); return Task.CompletedTask; } };
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        app.LoadOlder();                                   // no cursor yet
        Assert.Empty(asked);

        vm.SetHistoryCursor("#main", "cursor-1");
        app.LoadOlder();

        Assert.Equal("#main", Assert.Single(asked));
    }

    [Fact]
    public void PrependedHistoryIsAnnouncedToTheVirtualListAndRendered()
    {
        var (app, vm, _) = Build();
        for (var i = 0; i < 50; i++)
        {
            vm.Append("#main", "bob", $"recent {i}", 0, id: $"id-{i}");
        }

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        vm.Post(() => vm.Prepend("#main", Enumerable.Range(0, 40)
            .Select(i => ($"old-{i}", "carol", $"older {i}", (long)i)).ToList()));

        // Present drains the queue, calls VirtualListInserted with the count, then refreshes.
        app.Present(Width, Height);

        Assert.Equal(90, vm.Model.Messages.Count);
        Assert.Equal("older 0", vm.Model.Messages[0].Text);
        Assert.Equal(0, vm.TakePrependedCount());          // consumed by Present, not left pending

        doc.BuildDisplayList(Width, Height);
        var pixels = doc.RenderToPixels(Width, Height, SKColors.Black);
        Assert.Contains(pixels, b => b != 0);
    }

    [Fact]
    public void TypingIntoTheComposerReachesTheModel()
    {
        var (app, vm, _) = Build();
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        // Click into the composer near the bottom of the window, then type.
        doc.DispatchClick(500, Height - 40, 1);
        foreach (var ch in "hey")
        {
            doc.DispatchKey(ch.ToString(), EditKey.None);
        }

        output.WriteLine($"composer after typing: '{vm.Model.Composer}'");
        Assert.Equal("hey", vm.Model.Composer);
    }

    [Fact]
    public void StreamedReplyRendersWhileItIsStillArriving()
    {
        var (app, vm, _) = Build();
        for (var i = 0; i < 500; i++)
        {
            vm.Append("#main", "bob", $"backlog {i}", 0);
        }

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        vm.StreamStart("#main", "dagger", "s1");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 30; i++)
        {
            vm.StreamDelta("s1", $"tok{i} ");
            doc.Refresh();
            doc.BuildDisplayList(Width, Height);
        }

        sw.Stop();
        var perDelta = sw.Elapsed.TotalMilliseconds / 30;
        output.WriteLine($"per-token rebind + layout in a 500-message room: {perDelta:F3} ms");

        Assert.Contains("tok29", vm.Model.Messages[^1].Text);
        Assert.True(perDelta < 50, $"Per-delta cost {perDelta:F1} ms would stutter a token stream.");
    }
}
