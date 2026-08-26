using System.Text.Json;
using Banter.App;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Multi-line messages.
///
/// <para>CupriFace collapses newlines in bound text and ignores <c>white-space</c> entirely
/// (measured: <c>pre</c>, <c>pre-wrap</c> and <c>pre-line</c> all lay out identically to no rule
/// at all), so a message drawn as a single bound value comes out as one run-on line. The timeline
/// therefore renders a message a line at a time. These tests measure real laid-out height, because
/// that is the only thing that would have caught the original bug.</para>
/// </summary>
public sealed class MultiLineMessageTests(ITestOutputHelper output)
{
    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        return vm;
    }

    /// <summary>Laid-out height of the timeline's first message row.</summary>
    private static double RowHeight(ChatViewModel vm)
    {
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        using var dump = JsonDocument.Parse(doc.DebugDump(1100, 760));
        return FindTallestLine(dump.RootElement.GetProperty("tree"));
    }

    /// <summary>Deepest element carrying the message-row class, and its height.</summary>
    private static double FindTallestLine(JsonElement node)
    {
        var height = 0.0;

        // Match the class as a whole token: "timeline" contains "line", and matching on substring
        // measures the scroll container instead of the message and makes every case look equal.
        if (node.TryGetProperty("class", out var cls) &&
            cls.GetString() is { } name &&
            name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("line") &&
            node.TryGetProperty("box", out var box))
        {
            height = box[3].GetDouble();
        }

        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                height = Math.Max(height, FindTallestLine(child));
            }
        }

        return height;
    }

    [Fact]
    public void AMessageWithNewlinesIsTallerThanASingleLineOne()
    {
        var single = Room();
        single.Append("#main", "bob", "one line", 0);

        var multi = Room();
        multi.Append("#main", "bob", "line one\nline two\nline three", 0);

        var singleHeight = RowHeight(single);
        var multiHeight = RowHeight(multi);
        output.WriteLine($"single line: {singleHeight:F1}px, three lines: {multiHeight:F1}px");

        // Before the fix these were identical: the newlines collapsed to spaces.
        Assert.True(
            multiHeight > singleHeight * 2,
            $"A three-line message ({multiHeight:F1}px) should be far taller than one line ({singleHeight:F1}px).");
    }

    [Fact]
    public void ABlankLineStillTakesUpHeight()
    {
        var tight = Room();
        tight.Append("#main", "bob", "para one\npara two", 0);

        var spaced = Room();
        spaced.Append("#main", "bob", "para one\n\npara two", 0);

        // A paragraph break that renders as nothing is not a paragraph break.
        Assert.True(
            RowHeight(spaced) > RowHeight(tight),
            "A blank line between paragraphs should add height.");
    }

    [Fact]
    public void EveryLineIsPreservedInOrder()
    {
        var vm = Room();

        var row = vm.Append("#main", "dagger", "first\nsecond\nthird", 0);

        Assert.Equal(["first", "second", "third"], row.Lines.Select(l => l.Value));
    }

    [Fact]
    public void WindowsAndUnixLineEndingsBothSplit()
    {
        var vm = Room();

        var windows = vm.Append("#main", "bob", "a\r\nb", 0);
        var unix = vm.Append("#main", "bob", "a\nb", 0);

        // A message typed on Windows must not render as one line with a stray character.
        Assert.Equal(2, windows.Lines.Count);
        Assert.Equal(2, unix.Lines.Count);
        Assert.Equal("a", windows.Lines[0].Value);
        Assert.Equal("b", windows.Lines[1].Value);
    }

    [Fact]
    public void ASingleLineMessageIsStillOneLine()
    {
        var vm = Room();

        var row = vm.Append("#main", "bob", "just text", 0);

        Assert.Equal("just text", Assert.Single(row.Lines).Value);
    }

    [Fact]
    public void AStreamedReplyResplitsAsItGrows()
    {
        var vm = Room();
        vm.StreamStart("#main", "dagger", "s1");

        vm.StreamDelta("s1", "Here you go:\n");
        vm.StreamDelta("s1", "  line two\n");
        vm.StreamDelta("s1", "  line three");

        // Agent replies are the main source of multi-line text, and they arrive a token at a
        // time, so the split has to keep up rather than happen once at the end.
        var row = Assert.Single(vm.Model.Messages);
        Assert.Equal(3, row.Lines.Count);
        Assert.Equal("  line three", row.Lines[2].Value);
    }

    [Fact]
    public void AnAuthoritativeStreamEndResplitsToo()
    {
        var vm = Room();
        vm.StreamStart("#main", "dagger", "s1");
        vm.StreamDelta("s1", "partial");

        vm.StreamEnd("s1", "final\nwith\nthree lines", 0);

        Assert.Equal(3, Assert.Single(vm.Model.Messages).Lines.Count);
    }
}
