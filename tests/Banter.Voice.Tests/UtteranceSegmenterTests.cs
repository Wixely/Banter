using Bantz.Speech;
using Xunit;
using static Banter.Voice.Tests.Pcm;

namespace Banter.Voice.Tests;

/// <summary>
/// Where always-listening mode decides what counts as "something the user said". Getting the
/// boundaries wrong is worse than getting them late: a split sentence reaches the room as two
/// half-thoughts that agents then answer separately.
/// </summary>
public sealed class UtteranceSegmenterTests
{
    private static (UtteranceSegmenter Segmenter, List<PcmAudio> Utterances) Build(
        VoiceActivityOptions? options = null)
    {
        var segmenter = new UtteranceSegmenter(options);
        var utterances = new List<PcmAudio>();
        segmenter.UtteranceCompleted += utterances.Add;
        return (segmenter, utterances);
    }

    [Fact]
    public void SilenceProducesNothing()
    {
        var (segmenter, utterances) = Build();

        segmenter.Append(Quiet(Ms(5000)));
        segmenter.Flush();

        Assert.Empty(utterances);
    }

    [Fact]
    public void SpeechFollowedByAPauseIsOneUtterance()
    {
        var (segmenter, utterances) = Build();

        segmenter.Append(Concat(Quiet(Ms(500)), Speaking(Ms(1000)), Quiet(Ms(1500))));

        var one = Assert.Single(utterances);
        Assert.InRange(one.Duration.TotalMilliseconds, 1000, 1000 + 240 + 240 + 40);
    }

    [Fact]
    public void TwoSentencesSeparatedByAPauseAreTwoUtterances()
    {
        var (segmenter, utterances) = Build();

        segmenter.Append(Concat(
            Speaking(Ms(700)), Quiet(Ms(1200)),
            Speaking(Ms(700)), Quiet(Ms(1200))));

        Assert.Equal(2, utterances.Count);
    }

    [Fact]
    public void AShortPauseMidSentenceDoesNotSplitIt()
    {
        var (segmenter, utterances) = Build();

        // 400 ms is the pause people take to think; the gate waits 700 ms before deciding.
        segmenter.Append(Concat(
            Speaking(Ms(600)), Quiet(Ms(400)), Speaking(Ms(600)), Quiet(Ms(1200))));

        var one = Assert.Single(utterances);
        Assert.True(one.Duration > Ms(1500), $"expected one joined utterance, got {one.Duration}");
    }

    [Fact]
    public void ADoorSlamIsNotAnUtterance()
    {
        var (segmenter, utterances) = Build();

        segmenter.Append(Concat(Quiet(Ms(500)), Speaking(Ms(80)), Quiet(Ms(1500))));
        segmenter.Flush();

        Assert.Empty(utterances);
    }

    [Fact]
    public void SpeechStillInFlightIsEmittedOnFlush()
    {
        var (segmenter, utterances) = Build();

        segmenter.Append(Concat(Quiet(Ms(200)), Speaking(Ms(900))));
        Assert.Empty(utterances);

        segmenter.Flush();

        Assert.Single(utterances);
    }

    [Fact]
    public void ResetDropsSpeechInFlightWithoutEmittingIt()
    {
        var (segmenter, utterances) = Build();

        segmenter.Append(Speaking(Ms(900)));
        segmenter.Reset();
        segmenter.Flush();

        // The hard mute switch. Audio captured before it was pressed must not arrive after.
        Assert.Empty(utterances);
        Assert.False(segmenter.IsSpeaking);
    }

    [Fact]
    public void TheGateReportsWhetherItIsOpen()
    {
        var (segmenter, _) = Build();

        segmenter.Append(Quiet(Ms(200)));
        Assert.False(segmenter.IsSpeaking);

        segmenter.Append(Speaking(Ms(200)));
        Assert.True(segmenter.IsSpeaking);
    }

    [Fact]
    public void FramingDoesNotDependOnTheSizeOfTheChunksFedIn()
    {
        var audio = Concat(Quiet(Ms(300)), Speaking(Ms(800)), Quiet(Ms(1200)));

        var (whole, fromWhole) = Build();
        whole.Append(audio);
        whole.Flush();

        // A capture backend hands over whatever its buffer happened to hold; 331 bytes is an odd
        // number that straddles both sample and frame boundaries.
        var (chunked, fromChunks) = Build();
        for (var offset = 0; offset < audio.Length; offset += 331)
        {
            chunked.Append(audio.AsSpan(offset, Math.Min(331, audio.Length - offset)));
        }

        chunked.Flush();

        Assert.Equal(fromWhole.Count, fromChunks.Count);
        Assert.Equal(fromWhole[0].Data.Length, fromChunks[0].Data.Length);
        Assert.True(fromWhole[0].Data.Span.SequenceEqual(fromChunks[0].Data.Span));
    }

    [Fact]
    public void SomeoneWhoNeverPausesIsCutRatherThanBuffered()
    {
        var options = VoiceActivityOptions.Default with { MaxUtterance = Ms(500) };
        var (segmenter, utterances) = Build(options);

        segmenter.Append(Speaking(Ms(1600)));

        // Without the hard cut nothing reaches the room until they stop talking.
        Assert.True(utterances.Count >= 3, $"expected repeated cuts, got {utterances.Count}");
        Assert.All(utterances, u => Assert.True(u.Duration <= Ms(600), $"cut ran long: {u.Duration}"));
    }

    [Fact]
    public void TheTailAfterAForcedCutIsNotDiscardedForBeingShort()
    {
        var options = VoiceActivityOptions.Default with { MaxUtterance = Ms(500) };
        var (segmenter, utterances) = Build(options);

        segmenter.Append(Concat(Speaking(Ms(600)), Quiet(Ms(1200))));

        // The 100 ms left after the cut is under the min-speech gate, but it is the middle of a
        // word we already accepted — dropping it would eat the end of the sentence.
        Assert.Equal(2, utterances.Count);
    }

    [Fact]
    public void AFrameShorterThanOneSampleIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new UtteranceSegmenter(VoiceActivityOptions.Default with { FrameDuration = TimeSpan.Zero }));
    }
}
