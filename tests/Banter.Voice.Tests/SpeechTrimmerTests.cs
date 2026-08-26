using Bantz.Speech;
using Xunit;
using static Banter.Voice.Tests.Pcm;

namespace Banter.Voice.Tests;

/// <summary>
/// The trimmer decides what a push-to-talk press sends. Its refusals matter more than its
/// successes — sending near-silence to an engine puts an invented sentence in the room under the
/// user's name — so most of these cover what it declines.
/// </summary>
public sealed class SpeechTrimmerTests
{
    [Fact]
    public void SilenceTrimsToNothing()
    {
        Assert.Null(SpeechTrimmer.Trim(Audio(Quiet(Ms(2000)))));
    }

    [Fact]
    public void AnEmptyRecordingTrimsToNothing()
    {
        Assert.Null(SpeechTrimmer.Trim(Audio([])));
    }

    [Fact]
    public void ARecordingShorterThanOneFrameTrimsToNothing()
    {
        Assert.Null(SpeechTrimmer.Trim(Audio(Speaking(Ms(5)))));
    }

    [Fact]
    public void AnAccidentalPressTrimsToNothing()
    {
        // 100 ms of sound is under the 250 ms of voiced audio the gate asks for.
        var clip = Concat(Quiet(Ms(300)), Speaking(Ms(100)), Quiet(Ms(300)));

        Assert.Null(SpeechTrimmer.Trim(Audio(clip)));
    }

    [Fact]
    public void SpeechSurvivesAndTheSilenceAroundItDoesNot()
    {
        var clip = Concat(Quiet(Ms(1000)), Speaking(Ms(1000)), Quiet(Ms(1000)));

        var trimmed = SpeechTrimmer.Trim(Audio(clip));

        Assert.NotNull(trimmed);

        // A second of speech, plus one lead-in of padding at each end and up to a frame of
        // rounding. Well short of the three seconds that went in.
        Assert.InRange(trimmed.Duration.TotalMilliseconds, 1000, 1000 + (2 * 240) + 40);
    }

    [Fact]
    public void TheStartOfTheFirstWordIsKept()
    {
        var clip = Concat(Quiet(Ms(500)), Speaking(Ms(600)));

        var trimmed = SpeechTrimmer.Trim(Audio(clip));

        // Longer than the speech itself: the pre-roll is what stops the first consonant being
        // clipped off every transcript.
        Assert.NotNull(trimmed);
        Assert.True(trimmed.Duration > Ms(600), $"expected lead-in padding, got {trimmed.Duration}");
    }

    [Fact]
    public void SpeechWithNoSilenceAroundItIsLeftAlone()
    {
        var clip = Speaking(Ms(800));

        var trimmed = SpeechTrimmer.Trim(Audio(clip));

        Assert.NotNull(trimmed);
        Assert.Equal(clip.Length, trimmed.Data.Length);
    }

    [Fact]
    public void APauseInsideASentenceIsNotCutOut()
    {
        var clip = Concat(Speaking(Ms(400)), Quiet(Ms(200)), Speaking(Ms(400)));

        var trimmed = SpeechTrimmer.Trim(Audio(clip));

        // The trimmer takes the edges, never the middle: cutting the pause out would splice two
        // half-sentences together and change what was said.
        Assert.NotNull(trimmed);
        Assert.Equal(clip.Length, trimmed.Data.Length);
    }

    [Fact]
    public void ThresholdsAreConfigurable()
    {
        var clip = Concat(Quiet(Ms(300)), Speaking(Ms(100)), Quiet(Ms(300)));
        var lenient = VoiceActivityOptions.Default with { MinSpeechDuration = Ms(50) };

        Assert.Null(SpeechTrimmer.Trim(Audio(clip)));
        Assert.NotNull(SpeechTrimmer.Trim(Audio(clip), lenient));
    }
}
