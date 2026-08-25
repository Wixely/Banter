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
/// process</b> — machine speed cancels out — with only a loose wall-clock backstop.
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

    /// <summary>Median ms per call; median so one GC pause does not decide the result.</summary>
    private static double Median(Action action, int iterations = 15)
    {
        action();
        action();
        var samples = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return samples[iterations / 2];
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
    public void VirtualisedTimelineStaysWithinAFrameBudgetAtChatScale()
    {
        using var doc = new VirtualTimelineApp(Model(10_000)).CreateDocument();
        using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        var layoutMs = Median(() => doc.BuildDisplayList(Width, Height));
        var renderMs = Median(() =>
        {
            canvas.Clear(SKColors.Black);
            doc.Render(canvas, Width, Height);
        });

        output.WriteLine($"10,000-message room: layout {layoutMs:F3} ms, full paint {renderMs:F3} ms");

        // Loose CPU-raster backstop: catches a catastrophic regression, not a slow runner.
        Assert.True(renderMs < 100, $"Full CPU paint of a 10k-message room took {renderMs:F1} ms.");
    }

    [Fact]
    public void StreamingDeltasRebindFastEnoughForTokenRates()
    {
        // A room with real backscroll, receiving a streamed agent reply that grows per token.
        var model = Model(2_000);
        var streaming = new TimelineRow { Sender = "dagger", Text = "" };
        model.Messages.Add(streaming);
        using var doc = new VirtualTimelineApp(model).CreateDocument();

        var token = 0;
        var perDeltaMs = Median(() =>
        {
            streaming.Text += $"tok{token++} ";
            doc.Refresh();
            doc.BuildDisplayList(Width, Height);
        }, iterations: 40);

        output.WriteLine($"streaming delta (rebind + layout) in a 2,000-message room: {perDeltaMs:F3} ms");

        // A fast model emits ~50 tokens/s (20 ms budget). Assert well inside a lazier bound so
        // the gate is about order-of-magnitude sanity, not runner speed.
        Assert.True(perDeltaMs < 50, $"Per-delta rebind took {perDeltaMs:F1} ms — token streams would stutter.");
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
