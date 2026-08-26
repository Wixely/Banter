using Bantz.Speech;

namespace Banter.Voice;

/// <summary>
/// Cuts the silence off a finished recording — the push-to-talk half of PLAN §6's capture modes.
/// The user presses, thinks, speaks, and releases late, so the raw clip is mostly nothing; every
/// second of it is paid for again in upload and in transcription latency.
/// </summary>
public static class SpeechTrimmer
{
    /// <summary>
    /// Returns <paramref name="audio"/> narrowed to the speech in it, or <see langword="null"/>
    /// when it holds none worth sending.
    ///
    /// <para>Null is the accidental-press answer, and it is a return value rather than an empty
    /// buffer so a caller cannot forward it by mistake: an engine handed near-silence does not
    /// return nothing, it returns a confident hallucinated sentence, which then lands in the room
    /// under the user's name.</para>
    /// </summary>
    public static PcmAudio? Trim(PcmAudio audio, VoiceActivityOptions? options = null)
    {
        options ??= VoiceActivityOptions.Default;

        var frameBytes = AudioLevels.BytesFor(options.FrameDuration, audio.SampleRate) * audio.Channels;
        if (frameBytes <= 0 || audio.Data.Length < frameBytes)
        {
            return null;
        }

        var span = audio.Data.Span;
        var frames = span.Length / frameBytes;
        int first = -1, last = -1, voiced = 0;

        for (var i = 0; i < frames; i++)
        {
            var rms = AudioLevels.Rms(span.Slice(i * frameBytes, frameBytes));
            if (rms >= options.OnsetRms && first < 0)
            {
                first = i;
            }

            // Sustain uses the release threshold, so the quiet tail of a word counts as part of
            // it. Measuring the span with the onset threshold would clip every sentence twice.
            if (rms >= options.ReleaseRms)
            {
                last = i;
                if (first >= 0)
                {
                    voiced++;
                }
            }
        }

        if (first < 0 || last < first)
        {
            return null;
        }

        if (voiced * options.FrameDuration < options.MinSpeechDuration)
        {
            return null;
        }

        var lead = (int)Math.Ceiling(options.LeadIn / options.FrameDuration);
        var start = Math.Max(0, first - lead) * frameBytes;
        var end = Math.Min(span.Length, (Math.Min(frames, last + 1 + lead)) * frameBytes);

        return new PcmAudio(audio.Data[start..end], audio.SampleRate, audio.Channels);
    }
}
