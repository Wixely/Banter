using System.Text.Json;
using Banter.App;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Multi-line messages. Agent replies are mostly paragraphs and code, so hard newlines have to
/// survive rendering; the timeline styles message text <c>white-space: pre-wrap</c>.
///
/// <para>These measure real laid-out height rather than model state, because that is the only
/// thing that would have caught the original bug: a test asserting the text was stored correctly
/// passed happily while the screen showed one run-on line (CupriFace#69, fixed in 0.5.0).</para>
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

        // Before white-space was honoured these were identical: the newlines collapsed to spaces.
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
    public void TheTextIsKeptExactlyAsSent()
    {
        var vm = Room();

        var row = vm.Append("#main", "dagger", "first\nsecond\nthird", 0);

        // Rendered directly now, so the model holds the message verbatim.
        Assert.Equal("first\nsecond\nthird", row.Text);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    public void EveryLineEndingBreaks(string text)
    {
        // A message typed on Windows must not render as one line with a stray character.
        var single = Room();
        single.Append("#main", "bob", "ab", 0);

        var broken = Room();
        broken.Append("#main", "bob", text, 0);

        Assert.True(RowHeight(broken) > RowHeight(single), "This line ending should break the line.");
    }

    [Fact]
    public void ASingleLineMessageIsStillOneLine()
    {
        var oneLine = Room();
        oneLine.Append("#main", "bob", "just text", 0);

        var twoLines = Room();
        twoLines.Append("#main", "bob", "just\ntext", 0);

        Assert.True(RowHeight(twoLines) > RowHeight(oneLine));
    }

    [Fact]
    public void AStreamedReplyGrowsTallerAsLinesArrive()
    {
        var vm = Room();
        vm.StreamStart("#main", "dagger", "s1");
        vm.StreamDelta("s1", "Here you go:");
        var afterOneLine = RowHeight(vm);

        vm.StreamDelta("s1", "\n  line two");
        vm.StreamDelta("s1", "\n  line three");

        // Agent replies arrive a token at a time, so rendering has to keep up rather than only
        // be right once the stream ends.
        Assert.True(RowHeight(vm) > afterOneLine, "A streamed reply should grow as lines arrive.");
        Assert.Contains("line three", Assert.Single(vm.Model.Messages).Text);
    }

    [Fact]
    public void AnAuthoritativeStreamEndReplacesTheText()
    {
        var vm = Room();
        vm.StreamStart("#main", "dagger", "s1");
        vm.StreamDelta("s1", "partial");

        vm.StreamEnd("s1", "final\nwith\nthree lines", 0);

        var row = Assert.Single(vm.Model.Messages);
        Assert.Equal("final\nwith\nthree lines", row.Text);
        Assert.True(RowHeight(vm) > 40, "Three lines should be taller than one.");
    }
}
