using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// What agents write is drawn as Markdown, what people write is drawn as typed, and an image in
/// that Markdown is only ever one from Banter's own storage.
/// </summary>
public sealed class AgentMarkdownTests
{
    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.SetAgents("#main", [("local", true, "chat", true)]);
        return vm;
    }

    [Fact]
    public void AnAgentsReplyIsMarkdownAndAPersonsIsNot()
    {
        var vm = Room();
        var agent = vm.Append("#main", "local", "**done**", 0);
        var person = vm.Append("#main", "alice", "check snake_case_names", 0);

        Assert.Equal("md", agent.MarkdownClass);
        Assert.Equal("body hidden", agent.BodyClass);
        Assert.Equal("**done**", agent.MarkdownText);

        // A person's underscores are not italics: the app does not rewrite what people say.
        Assert.Equal("md hidden", person.MarkdownClass);
        Assert.Equal("body", person.BodyClass);
    }

    [Fact]
    public void HistoryFromBeforeTheRosterIsRedrawnOnceItIsKnownToBeAnAgents()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        var early = vm.Append("#main", "scout", "- one\n- two", 0);
        Assert.Equal("md hidden", early.MarkdownClass);

        vm.SetAgents("#main", [("scout", false, "web", false)]);

        Assert.Equal("md", early.MarkdownClass);
    }

    [Fact]
    public void AStreamedReplyIsMarkdownAsItGrows()
    {
        var vm = Room();
        vm.StreamStart("#main", "local", "s1");
        vm.StreamDelta("s1", "# Head");
        var row = vm.Model.Messages[^1];
        Assert.Equal("md", row.MarkdownClass);
        Assert.Equal("# Head", row.MarkdownText);

        vm.StreamEnd("s1", "# Head\nbody", 0);
        Assert.Equal("# Head\nbody", row.MarkdownText);
    }

    [Fact]
    public void ARemoteImageIsALinkAndNeverLoaded()
    {
        var vm = Room();
        var drawn = vm.MarkdownFor("look ![a cat](https://example.com/cat.png)");

        Assert.DoesNotContain("![", drawn);
        Assert.Equal("look [a cat (remote image, not loaded)](https://example.com/cat.png)", drawn);
    }

    [Fact]
    public void AStoredImageLoadsThenShowsFromTheLocalCopy()
    {
        var vm = Room();
        var row = vm.Append("#main", "local", "![chart](banter:f1)", 0);
        Assert.Equal("*chart (loading)*", row.MarkdownText);

        var path = Path.Combine(Path.GetTempPath(), "banter-md-f1.png");
        vm.SetInlineImage("f1", path);

        Assert.Equal($"![chart]({new Uri(path).AbsoluteUri})", row.MarkdownText);
    }

    [Fact]
    public void AStoredImageThatCannotBeShownSaysSoRatherThanLoadingForever()
    {
        var vm = Room();
        var row = vm.Append("#main", "local", "![chart](banter:gone)", 0);

        vm.StoredImageUnavailable("gone");

        Assert.Equal("*chart (image unavailable)*", row.MarkdownText);
    }

    [Theory]
    [InlineData("banter:f1", "f1")]
    [InlineData("banter://abc_DEF-9", "abc_DEF-9")]
    [InlineData("banter:../../etc/passwd", null)]
    [InlineData("banter:", null)]
    [InlineData("https://example.com/x.png", null)]
    [InlineData("file:///C:/secret.png", null)]
    public void OnlyABanterFileIdIsAStoredImage(string src, string? id) =>
        Assert.Equal(id, ChatViewModel.StoredImageId(src));

    [Fact]
    public void TheFilesAMessageShowsAreTheOnesFetched() =>
        Assert.Equal(
            ["a", "b"],
            ChatViewModel.StoredImages("![x](banter:a) ![y](https://e.com/z.png) ![z](banter:b) ![w](banter:a)"));
}
