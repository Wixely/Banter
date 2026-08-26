using Xunit;

namespace Banter.Voice.Tests;

/// <summary>
/// What decides when the first sound of an agent's reply is heard. A split too eager speaks half
/// a number; one too lazy leaves the room in silence while a model streams.
/// </summary>
public sealed class SentenceSegmenterTests
{
    /// <summary>Feeds text the way a model streams it — a few characters at a time.</summary>
    private static List<string> Stream(string text, int deltaSize = 4, SentenceSegmenterOptions? options = null)
    {
        var segmenter = new SentenceSegmenter(options);
        var sentences = new List<string>();
        for (var offset = 0; offset < text.Length; offset += deltaSize)
        {
            sentences.AddRange(segmenter.Append(text.Substring(offset, Math.Min(deltaSize, text.Length - offset))));
        }

        if (segmenter.Flush() is { } rest)
        {
            sentences.Add(rest);
        }

        return sentences;
    }

    [Fact]
    public void SentencesComeOutOneAtATime()
    {
        var sentences = Stream("The board has three open tasks. Two are claimed. One is not.");

        Assert.Equal(
            ["The board has three open tasks.", "Two are claimed.", "One is not."],
            sentences);
    }

    [Fact]
    public void ASentenceIsHeldUntilSomethingFollowsItsFullStop()
    {
        var segmenter = new SentenceSegmenter();

        // "3." could still become "3.5", and speaking half a number is worse than one more delta.
        Assert.Empty(segmenter.Append("The lease is 3."));
        Assert.Empty(segmenter.Append("5"));
        Assert.Equal(["The lease is 3.5 hours long."], segmenter.Append(" hours long. And"));
    }

    [Fact]
    public void DecimalsDoNotEndSentences()
    {
        var sentences = Stream("Version 1.7.0 shipped. It fixed 4 things.");

        Assert.Equal(["Version 1.7.0 shipped.", "It fixed 4 things."], sentences);
    }

    [Fact]
    public void AbbreviationsAndInitialsDoNotEndSentences()
    {
        Assert.Equal(
            ["Dr. Who asked about it.", "So did J. R. Hartley."],
            Stream("Dr. Who asked about it. So did J. R. Hartley."));
    }

    [Fact]
    public void QuestionsAndExclamationsEndSentences()
    {
        Assert.Equal(
            ["Is it done?", "It is!", "Nearly."],
            Stream("Is it done? It is! Nearly."));
    }

    [Fact]
    public void AnEllipsisEndsASentenceOnlyWhenWhatFollowsStartsOne()
    {
        // The same three dots are a pause or an ending depending on what comes next, and it is
        // the one case where waiting a word to find out is worth it.
        Assert.Equal(["Well... mostly."], Stream("Well... mostly."));
        Assert.Equal(["I am not sure...", "Ask the warden."], Stream("I am not sure... Ask the warden."));
    }

    [Fact]
    public void ClosingQuotesStayWithTheSentenceTheyClose()
    {
        Assert.Equal(
            ["He said \"it is done.\"", "Then he left."],
            Stream("He said \"it is done.\" Then he left."));
    }

    [Fact]
    public void TheLastSentenceArrivesOnFlush()
    {
        var segmenter = new SentenceSegmenter();
        segmenter.Append("All three tasks are done");

        Assert.True(segmenter.HasPending);
        Assert.Equal("All three tasks are done", segmenter.Flush());
        Assert.False(segmenter.HasPending);
    }

    [Fact]
    public void FlushingAFinishedStreamReturnsNothing()
    {
        var segmenter = new SentenceSegmenter();
        segmenter.Append("Done. ");

        // The terminator plus the space already handed that sentence over.
        Assert.Null(segmenter.Flush());
    }

    [Fact]
    public void TextThatNeverPunctuatesItselfIsStillBrokenUp()
    {
        var options = SentenceSegmenterOptions.Default with { MaxCharacters = 40 };
        var wall = string.Join(' ', Enumerable.Repeat("and then", 30));

        var sentences = Stream(wall, options: options);

        // Without this an agent can stream for a minute and be spoken only once it stops.
        Assert.True(sentences.Count > 1, "expected the wall of text to be broken up");
        Assert.All(sentences, s => Assert.True(s.Length <= 40, $"chunk of {s.Length} characters"));
    }

    [Fact]
    public void AForcedBreakLandsOnAWordBoundary()
    {
        var options = SentenceSegmenterOptions.Default with { MaxCharacters = 20 };

        var sentences = Stream("counting one two three four five six seven eight", options: options);

        Assert.All(sentences, s => Assert.DoesNotContain("  ", s));
        Assert.DoesNotContain(sentences, s => s.EndsWith("thr") || s.EndsWith("sev"));
    }

    [Fact]
    public void TheSizeOfTheDeltasDoesNotChangeTheSentences()
    {
        const string reply = "I checked the board. Version 2.1 is out; Dr. Lee approved it. Done!";

        // A terminator landing exactly on a delta boundary is the case that breaks naive splitting.
        var oneAtATime = Stream(reply, deltaSize: 1);
        var inChunks = Stream(reply, deltaSize: 7);
        var allAtOnce = Stream(reply, deltaSize: reply.Length);

        Assert.Equal(oneAtATime, inChunks);
        Assert.Equal(oneAtATime, allAtOnce);
        Assert.Equal(3, oneAtATime.Count);
    }

    [Fact]
    public void NothingIsLost()
    {
        const string reply = "One. Two! Three? Four... and five";

        var rejoined = string.Concat(Stream(reply, deltaSize: 3).Select(s => s + " ")).Trim();

        Assert.Equal(reply.Replace("  ", " "), rejoined);
    }
}
