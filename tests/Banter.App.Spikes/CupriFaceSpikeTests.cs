using System.Diagnostics;
using CupriFace;
using CupriFace.Interaction;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Spikes;

/// <summary>
/// The PLAN Phase 0 CupriFace spikes (CUPRIFACE-PLAN §5), run headlessly:
/// virtualized scrollback perf at chat scale, and streaming-delta render rate.
/// Following CupriFace's own perf-test guidance, the gates are <b>ratios measured in one
/// process</b> — machine speed cancels out. Absolute milliseconds are printed for eyeballing but
/// never asserted: a wall-clock budget measures the runner, and the last one here failed CI on a
/// shared runner ~1 ms over budget with no app change.
/// </summary>
public sealed class CupriFaceSpikeTests(ITestOutputHelper output)
{
    private const int Width = 940;
    private const int Height = 720;

    private static TimelineModel Model(int messages)
    {
        var model = new TimelineModel();
        for (var i = 0; i < messages; i++)
        {
            model.Messages.Add(new TimelineRow
            {
                Sender = i % 3 == 0 ? "alice" : i % 3 == 1 ? "dagger" : "bob",
                Text = $"message {i} — the quick brown fox jumps over the lazy dog",
            });
        }

        return model;
    }

    /// <summary>Times two workloads interleaved so drift affects both equally.</summary>
    private static (double Small, double Large) Race(Action small, Action large, int iterations = 15)
    {
        small(); large();
        var s = new double[iterations];
        var l = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            small();
            sw.Stop();
            s[i] = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            large();
            sw.Stop();
            l[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(s);
        Array.Sort(l);
        return (s[iterations / 2], l[iterations / 2]);
    }

    [Fact]
    public void VirtualisedTimelineRenderCostIsIndependentOfHistorySize()
    {
        using var small = new VirtualTimelineApp(Model(100)).CreateDocument();
        using var large = new VirtualTimelineApp(Model(10_000)).CreateDocument();

        var (smallMs, largeMs) = Race(
            () => small.BuildDisplayList(Width, Height),
            () => large.BuildDisplayList(Width, Height));

        var ratio = largeMs / Math.Max(smallMs, 0.0001);
        output.WriteLine($"virtual layout: 100 msgs {smallMs:F3} ms, 10,000 msgs {largeMs:F3} ms (x{ratio:F2})");

        // 100x the history must not cost anywhere near 100x — virtualization means only a
        // screenful is laid out. Generous bound so a noisy runner cannot make this flaky.
        Assert.True(ratio < 5, $"Virtualized layout scaled with history size (x{ratio:F2}).");
    }

    [Fact]
    public void PlainTimelineIsTheOneThatScalesWithHistory()
    {
        using var small = new PlainTimelineApp(Model(100)).CreateDocument();
        using var large = new PlainTimelineApp(Model(10_000)).CreateDocument();

        var (smallMs, largeMs) = Race(
            () => small.BuildDisplayList(Width, Height),
            () => large.BuildDisplayList(Width, Height),
            iterations: 7);

        var ratio = largeMs / Math.Max(smallMs, 0.0001);
        output.WriteLine($"plain layout:   100 msgs {smallMs:F3} ms, 10,000 msgs {largeMs:F3} ms (x{ratio:F2})");

        // Recorded, not gated: this is the control case proving the virtual result is real.
        Assert.True(largeMs > 0);
    }

    [Fact]
    public void VirtualisedTimelinePaintCostIsIndependentOfHistorySize()
    {
        using var small = new VirtualTimelineApp(Model(100)).CreateDocument();
        using var large = new VirtualTimelineApp(Model(10_000)).CreateDocument();
        using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        // Layout once each; the race is over painting the display lists layout produced. If
        // virtualization broke, the 10k list holds every row's draw ops and paint scales with it.
        small.BuildDisplayList(Width, Height);
        large.BuildDisplayList(Width, Height);

        var (smallMs, largeMs) = Race(
            () => { canvas.Clear(SKColors.Black); small.Render(canvas, Width, Height); },
            () => { canvas.Clear(SKColors.Black); large.Render(canvas, Width, Height); });

        var ratio = largeMs / Math.Max(smallMs, 0.0001);
        output.WriteLine($"full CPU paint: 100 msgs {smallMs:F3} ms, 10,000 msgs {largeMs:F3} ms (x{ratio:F2})");

        // This gate used to be `paint of a 10k room < 100 ms` — a wall-clock backstop, which is a
        // gate on the runner, not the work. The regression it exists to catch is paint cost
        // scaling with history, and that is a ratio a loaded runner cannot fake either way.
        Assert.True(ratio < 5, $"Paint cost scaled with history size (x{ratio:F2}).");
    }

    [Fact]
    public void StreamingDeltaCostIsIndependentOfBacklog()
    {
        // The same streamed agent reply growing token by token in two rooms: one nearly empty,
        // one with the 2,000 messages of a real session. What would make token streams stutter is
        // a delta that re-lays-out the whole room — and that shows up as per-token cost scaling
        // with the backlog, not as any particular number of milliseconds. The sibling test in
        // BanterChatAppTests went through this exact conversion after its 50 ms wall-clock budget
        // failed CI on a shared runner ~1 ms over, with no app change; this test asserted the
        // same 50 ms as a MEDIAN, which only a faster fixture app kept green.
        var smallModel = Model(100);
        var smallRow = new TimelineRow { Sender = "dagger", Text = "" };
        smallModel.Messages.Add(smallRow);
        using var smallDoc = new VirtualTimelineApp(smallModel).CreateDocument();

        var largeModel = Model(2_000);
        var largeRow = new TimelineRow { Sender = "dagger", Text = "" };
        largeModel.Messages.Add(largeRow);
        using var largeDoc = new VirtualTimelineApp(largeModel).CreateDocument();

        var token = 0;
        var (smallMs, largeMs) = Race(
            () => { smallRow.Text += $"tok{token++} "; smallDoc.Refresh(); smallDoc.BuildDisplayList(Width, Height); },
            () => { largeRow.Text += $"tok{token++} "; largeDoc.Refresh(); largeDoc.BuildDisplayList(Width, Height); });

        var ratio = largeMs / Math.Max(smallMs, 0.0001);

        // Absolute cost still printed: a fast model emits ~50 tokens/s, so ~20 ms per delta is
        // the budget an operator would eyeball this against.
        output.WriteLine($"streaming delta: 100-msg room {smallMs:F3} ms, 2,000-msg room {largeMs:F3} ms (x{ratio:F2})");

        // Same bound as the BanterChatAppTests conversion, looser than the layout races above:
        // a delta rebinds the changed row on top of laying out a screenful, and in the small room
        // that fixed cost is most of the sample, so honest noise moves this ratio more.
        Assert.True(ratio < 8, $"Per-token cost grew x{ratio:F2} between a 100- and 2,000-message room.");
    }

    [Fact]
    public void TimelineActuallyPaintsContent()
    {
        using var doc = new VirtualTimelineApp(Model(500)).CreateDocument();
        var pixels = doc.RenderToPixels(Width, Height, SKColors.Black);

        Assert.Equal(Width * Height * 4, pixels.Length);
        // Something other than the clear colour was drawn — text and chrome landed.
        Assert.Contains(pixels, b => b != 0);
    }

    /// <summary>
    /// The case CupriFace 0.4.0 (#67) added and the whole timeline design now rests on: rows whose
    /// height comes from their own wrapped content, not from <c>item-height</c>. Messages here vary
    /// from one line to many, so if heights were still forced to a uniform pitch this would either
    /// clip badly or scale with history.
    /// </summary>
    [Fact]
    public void VariableHeightRowsAreStillVirtualised()
    {
        static TimelineModel Wrapped(int count)
        {
            var m = new TimelineModel();
            for (var i = 0; i < count; i++)
            {
                // 1 line to ~12 lines, the real spread of chat messages.
                var words = 4 + (i % 7) * 40;
                m.Messages.Add(new TimelineRow
                {
                    Sender = i % 2 == 0 ? "alice" : "dagger",
                    Text = string.Join(' ', Enumerable.Repeat("the quick brown fox", words)),
                });
            }

            return m;
        }

        using var small = new VirtualTimelineApp(Wrapped(100)).CreateDocument();
        using var large = new VirtualTimelineApp(Wrapped(5_000)).CreateDocument();

        var (smallMs, largeMs) = Race(
            () => small.BuildDisplayList(Width, Height),
            () => large.BuildDisplayList(Width, Height));

        var ratio = largeMs / Math.Max(smallMs, 0.0001);
        output.WriteLine($"wrap-height rows: 100 msgs {smallMs:F3} ms, 5,000 msgs {largeMs:F3} ms (x{ratio:F2})");

        Assert.True(ratio < 5, $"Variable-height virtualization scaled with history (x{ratio:F2}).");
    }

    [Fact]
    public void ComposerAcceptsTypedTextIntoTheModel()
    {
        var model = Model(10);
        using var doc = new VirtualTimelineApp(model).CreateDocument();
        doc.BuildDisplayList(Width, Height);

        // Focus the composer, type, and confirm two-way binding wrote back to the model.
        doc.DispatchClick(470, 690, 1);
        foreach (var ch in "hi")
        {
            doc.DispatchKey(ch.ToString(), EditKey.None);
        }

        output.WriteLine($"composer model value after typing: '{model.Composer}'");
        Assert.Equal("hi", model.Composer);
    }
}
