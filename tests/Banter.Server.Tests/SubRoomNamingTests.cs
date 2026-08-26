using Banter.Agents.Sdk;
using Xunit;

namespace Banter.Server.Tests;

/// <summary>
/// Names for rooms agents open. A room list of identifiers is unreadable; a room list of things
/// being done is a status board.
/// </summary>
public sealed class SubRoomNamingTests
{
    [Theory]
    [InlineData("fix the parser bug in the tokenizer", "parser")]
    [InlineData("what does everyone think about the Thompson matter", "thompson")]
    [InlineData("search github for the open issue about caching", "github")]
    public void TheNameIsDrawnFromTheRequest(string prompt, string expectedWord)
    {
        var name = BanterAgent.SubRoomName("#main", prompt);

        Assert.StartsWith("#", name);
        Assert.Contains(expectedWord, name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FillerWordsAreLeftOut()
    {
        var name = BanterAgent.SubRoomName("#main", "can you please look at the caching problem");

        // "can you please the" carry no meaning in a room name.
        Assert.DoesNotContain("please", name);
        Assert.DoesNotContain("-you-", name);
        Assert.Contains("caching", name);
    }

    [Fact]
    public void TwoSimilarRequestsDoNotCollide()
    {
        var first = BanterAgent.SubRoomName("#main", "fix the parser");
        var second = BanterAgent.SubRoomName("#main", "fix the parser");

        // The readable part matches; only the collision suffix differs.
        Assert.NotEqual(first, second);
        Assert.StartsWith("#fix-parser-", first);
        Assert.StartsWith("#fix-parser-", second);
    }

    [Fact]
    public void ARequestWithNothingUsableFallsBackToTheParentName()
    {
        var name = BanterAgent.SubRoomName("#main", "?? !! ...");

        Assert.StartsWith("#main-", name);
    }

    [Fact]
    public void PunctuationNeverReachesTheName()
    {
        var name = BanterAgent.SubRoomName("#main", "review PR #42: \"the fix\" (urgent!) @alice");

        // A room name the server would reject as invalid is worse than an ugly one.
        Assert.Equal('#', name[0]);
        Assert.DoesNotContain(name[1..], c => !char.IsLetterOrDigit(c) && c != '-');
    }

    [Fact]
    public void LongRequestsAreTrimmedToSomethingReadable()
    {
        var name = BanterAgent.SubRoomName(
            "#main",
            "investigate the intermittent deserialization failure affecting downstream consumers of the ledger");

        Assert.True(name.Length <= 40, $"'{name}' is too long for a room list.");
    }
}
