using Banter.App;
using CupriFace;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// A long room name stays inside its own box.
///
/// <para>A flex item's BOX shrinks and its glyphs do not: the engine lays text out at its natural
/// width and paints it, so "#a-rather-long-room-name" printed straight across the "12 members"
/// beside it and the two were drawn on top of each other. Reported from a phone, where the column
/// is narrowest and every name is long relative to it.</para>
///
/// <para>Two rules together: <c>min-width: 0</c> lets the box shrink at all, and
/// <c>overflow: hidden</c> is what stops what is in it painting out - the painter clips a node's
/// children whenever overflow is not visible, which is the whole mechanism. The engine has no
/// <c>text-overflow</c>, so this clips rather than ellipsises.</para>
/// </summary>
public sealed class RoomNameFitTests(ITestOutputHelper output)
{
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

    private static RenderNode First(CupriDocument doc, string cls) =>
        Walk(doc.Root).First(n =>
            n.Element?.GetAttribute("class")?.Split(' ').Contains(cls) == true
            && n.Width > 0 && n.Height > 0);

    private static CupriDocument OnAPhone(out BanterChatApp app)
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#a-rather-long-room-name-for-a-phone");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.Connected("tcp://h:7770", "alice");

        // A message in the room you are NOT in, which is what puts an unread badge beside its
        // name - the thing the long name used to be painted over.
        vm.Append("#a-rather-long-room-name-for-a-phone", "bob", "anyone about?", 0);
        vm.SetRoomListing([
            ("#main", null, 2),
            ("#a-rather-long-room-name-for-a-phone", null, 12),
            ("#another-one-nobody-would-shorten", null, 3),
        ]);
        vm.ToggleRooms(narrow: true);

        app = new BanterChatApp(vm);
        var doc = app.CreateDocument();
        doc.InputProfile = InputProfile.Touch;
        doc.Refresh();
        var p = BanterChatApp.Presentation(412, 915);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
        return doc;
    }

    /// <summary>
    /// The name and the member count do not share any horizontal space, and the name is clipped -
    /// which is what keeps the glyphs inside the box the first half of this measures.
    /// </summary>
    [Fact]
    public void ALongRoomNameDoesNotRunIntoTheMemberCount()
    {
        using var doc = OnAPhone(out _);

        var name = First(doc, "browse-name");
        var members = First(doc, "browse-members");
        var nameBox = HitTesting.AbsoluteBox(name);
        var memberBox = HitTesting.AbsoluteBox(members);

        output.WriteLine($"name  {nameBox.X:F0}..{nameBox.X + nameBox.W:F0} overflow={name.Style.Overflow}");
        output.WriteLine($"count {memberBox.X:F0}..{memberBox.X + memberBox.W:F0}");

        Assert.True(
            nameBox.X + nameBox.W <= memberBox.X + 0.5f,
            $"the name ends at {nameBox.X + nameBox.W:F0} and the count starts at {memberBox.X:F0}");

        // The box shrinking is only half of it. Without this the text is laid out at its own
        // width and painted straight over the count, which is what was reported.
        Assert.NotEqual(OverflowMode.Visible, name.Style.Overflow);

        // And the count keeps its own width rather than being squeezed by the name.
        Assert.True(memberBox.W > 1f, $"the member count laid out {memberBox.W:F0} wide");
    }

    /// <summary>The joined-room tab has the same shape of problem with its unread badge, and the
    /// same pair of rules. A fix applied to one of two lists is the kind that looks complete.</summary>
    [Fact]
    public void ALongRoomNameDoesNotRunIntoTheUnreadBadge()
    {
        using var doc = OnAPhone(out _);

        var tab = Walk(doc.Root)
            .Where(n => n.Element?.GetAttribute("class")?.Split(' ').Contains("tab-name") == true)
            .First(n => n.Children.Any(c => c.IsText && c.Text!.Contains("long-room-name", StringComparison.Ordinal)));

        var badge = Walk(doc.Root).FirstOrDefault(n =>
            n.Element?.GetAttribute("class")?.Split(' ').Contains("badge") == true && n.Width > 0);

        var nameBox = HitTesting.AbsoluteBox(tab);
        output.WriteLine($"tab {nameBox.X:F0}..{nameBox.X + nameBox.W:F0} overflow={tab.Style.Overflow}");

        Assert.NotEqual(OverflowMode.Visible, tab.Style.Overflow);

        if (badge is not null)
        {
            var badgeBox = HitTesting.AbsoluteBox(badge);
            output.WriteLine($"badge {badgeBox.X:F0}..{badgeBox.X + badgeBox.W:F0}");
            Assert.True(
                nameBox.X + nameBox.W <= badgeBox.X + 0.5f,
                $"the name ends at {nameBox.X + nameBox.W:F0} and the badge starts at {badgeBox.X:F0}");
        }
    }
}
