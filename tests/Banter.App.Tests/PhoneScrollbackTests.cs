using Banter.App;
using CupriFace;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Reading back through a room on a phone stays where you are reading.
///
/// <para>On Android the timeline jumped to the newest message on the next scroll after a message
/// arrived or a page of history loaded. The host rebuilds the document on its own 20 Hz cadence
/// as well as whenever the app changes, and when both landed in one frame the second rebuild met
/// a tree no layout had reached yet: CupriFace read its scroll limit as 0, took any offset for
/// the bottom, and the bottom-anchored timeline was put back at the end. Fixed in CupriFace
/// 0.44.1. A desktop lays out between rebuilds, which is why it was only ever seen on a phone -
/// so these drive a frame the way AndroidHost does rather than the way a test usually does.</para>
/// </summary>
public sealed class PhoneScrollbackTests(ITestOutputHelper output)
{
    private const int W = 412;
    private const int H = 915;

    private sealed class Phone
    {
        public required BanterChatApp App { get; init; }
        public required ChatViewModel Vm { get; init; }
        public required CupriDocument Doc { get; init; }
        public TouchDriver Touch { get; set; } = null!;
        private double _lastRefresh;

        public RenderNode Timeline => Walk(Doc.Root).First(n => n.Element?.GetAttribute("class")?.Split(' ').Contains("timeline") == true);

        public void Run(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                Touch.Advance(1.0 / 60);
                Frame();
            }
        }

        /// <summary>AndroidHost.PaintFrame, in its order: present, the refresh cadence, the
        /// animation clock (which is what moves a fling), then layout.</summary>
        public void Frame(bool refreshCadenceDue = false)
        {
            var p = App.Present(W, H);
            if (refreshCadenceDue || Touch.Now - _lastRefresh >= App.RefreshIntervalSeconds)
            {
                _lastRefresh = Touch.Now;
                Doc.Refresh();
            }

            if (Doc.HasAnimations || Doc.HasActiveTransitions)
            {
                Doc.Animate(Touch.Now);
            }

            Doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
        }
    }

    private static IEnumerable<RenderNode> Walk(RenderNode n)
    {
        yield return n;
        foreach (var c in n.Children)
        {
            foreach (var d in Walk(c))
            {
                yield return d;
            }
        }
    }

    private static Phone InARoomWithHistory(int messages)
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.Connected("tcp://host:7770", "alice");
        for (var i = 0; i < messages; i++)
        {
            vm.Append("#main", i % 3 == 0 ? "local" : "bob", $"history {i} with enough text to wrap once on a phone screen at this width", 0, id: $"h{i}");
        }

        var app = new BanterChatApp(vm);
        var doc = app.CreateDocument();
        doc.InputProfile = InputProfile.Touch;
        doc.Refresh();
        var phone = new Phone { App = app, Vm = vm, Doc = doc };
        phone.Touch = new TouchDriver(doc, onFrame: () => phone.Frame());
        phone.Run(5);
        return phone;
    }

    [Fact]
    public void ReadingBackThroughSeveralPagesOfHistoryStaysWhereYouAre()
    {
        var phone = InARoomWithHistory(100);

        for (var page = 1; page <= 3; page++)
        {
            phone.Touch.Fling(200, 300, dy: 500);
            phone.Run(60);
            var up = phone.Timeline;
            Assert.True(up.ScrollY < up.MaxScrollY - 200, $"page {page}: the fling never left the bottom");

            // What a session posts when "earlier messages" is tapped.
            var batch = Enumerable.Range(0, 100)
                .Select(i => ($"p{page}-{i}", "bob", $"older {page}.{i} with enough text to wrap once on a phone screen at this width", 0L, ""))
                .ToList();
            phone.Vm.Post(() => phone.Vm.Prepend("#main", batch));
            phone.Run(10);

            var t = phone.Timeline;
            output.WriteLine($"page {page}: {up.ScrollY:F0} of {up.MaxScrollY:F0} -> {t.ScrollY:F0} of {t.MaxScrollY:F0}");
            Assert.True(t.ScrollY < t.MaxScrollY - 200, $"page {page}: loading older history put the reader at the bottom ({t.ScrollY:F0} of {t.MaxScrollY:F0})");
        }
    }

    [Fact]
    public void AMessageArrivingWhileYouReadBackDoesNotPullYouDown()
    {
        var phone = InARoomWithHistory(150);
        phone.Touch.Fling(200, 300, dy: 600);
        phone.Run(60);
        var before = phone.Timeline;
        var reading = before.ScrollY;
        Assert.True(reading < before.MaxScrollY - 200, "the fling never left the bottom, so this would prove nothing");

        // A message arrives, and the host's refresh falls due in the same frame the app rebuilds
        // for it - two rebuilds before one layout.
        phone.Vm.Post(() => phone.Vm.Append("#main", "local", "a reply arriving while you read", 0, id: "new"));
        phone.Frame(refreshCadenceDue: true);
        phone.Run(5);

        var t = phone.Timeline;
        output.WriteLine($"reading at {reading:F0}; after the message {t.ScrollY:F0} of {t.MaxScrollY:F0}");
        Assert.True(t.ScrollY < t.MaxScrollY - 200, $"a new message pulled the reader to the bottom ({t.ScrollY:F0} of {t.MaxScrollY:F0})");
        Assert.Equal(reading, t.ScrollY, 30f);
    }
}
