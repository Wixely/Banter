using Bantz.Speech;

namespace Banter.Voice;

/// <summary>
/// Thresholds for the managed energy gate (PLAN §6). Deliberately not a model: a pure-C# RMS
/// gate with hysteresis is enough to trim a push-to-talk clip and to cut always-listening into
/// utterances, and it costs nothing to run on a phone. Silero VAD through ONNX stays the opt-in
/// upgrade rather than a dependency of the core.
/// </summary>
public sealed record VoiceActivityOptions
{
    public static VoiceActivityOptions Default { get; } = new();

    /// <summary>
    /// Analysis window. 20 ms is short enough to land on a word boundary and long enough that a
    /// single glitchy sample cannot move the RMS.
    /// </summary>
    public TimeSpan FrameDuration { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// RMS (full scale 1.0) at which silence becomes speech. Matches
    /// <c>Bantz.Capture.AudioSignalAnalyzer.ActiveRmsThreshold</c> so that the level meter a user
    /// watches and the gate that cuts their audio agree on what counts as sound.
    /// </summary>
    public double OnsetRms { get; init; } = 0.012;

    /// <summary>
    /// RMS at which speech becomes silence again. Lower than <see cref="OnsetRms"/> on purpose: a
    /// single threshold chatters open and shut through the pauses between words, and every
    /// chatter is a severed utterance.
    /// </summary>
    public double ReleaseRms { get; init; } = 0.006;

    /// <summary>
    /// Voiced audio an utterance must contain before it is worth transcribing. Below this it is a
    /// door, a cough, or a key pressed by accident — all of which cost a round trip and produce a
    /// hallucinated sentence in the room.
    /// </summary>
    public TimeSpan MinSpeechDuration { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Quiet needed to declare an utterance finished. Long enough to survive the pause
    /// mid-sentence that people take to think.</summary>
    public TimeSpan TrailingSilence { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Audio kept from before the gate opened. The onset frame is already part-way into the first
    /// consonant, so without a pre-roll every utterance starts clipped and every transcript loses
    /// its first word.
    /// </summary>
    public TimeSpan LeadIn { get; init; } = TimeSpan.FromMilliseconds(240);

    /// <summary>
    /// A hard cut for someone who never pauses. Without it a continuous talker produces one
    /// unbounded buffer and nothing reaches the room until they stop.
    /// </summary>
    public TimeSpan MaxUtterance { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Frame measurements over signed 16-bit PCM.</summary>
public static class AudioLevels
{
    /// <summary>Bytes per sample of the signed 16-bit format every Bantz speech engine takes.</summary>
    public const int BytesPerSample = 2;

    /// <summary>
    /// Root-mean-square amplitude of a PCM window, normalised to full scale 1.0. A trailing odd
    /// byte is ignored rather than treated as a sample — a half-written frame is a framing bug,
    /// not a loud one.
    /// </summary>
    public static double Rms(ReadOnlySpan<byte> pcm16)
    {
        var samples = pcm16.Length / BytesPerSample;
        if (samples == 0)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < samples; i++)
        {
            double sample = (short)(pcm16[i * 2] | (pcm16[(i * 2) + 1] << 8));
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples) / short.MaxValue;
    }

    /// <summary>Bytes holding <paramref name="duration"/> of audio at this rate, rounded to whole samples.</summary>
    public static int BytesFor(TimeSpan duration, int sampleRate = PcmAudio.SpeechSampleRate) =>
        (int)Math.Round(duration.TotalSeconds * sampleRate) * BytesPerSample;

    /// <summary>The duration <paramref name="byteCount"/> of audio represents at this rate.</summary>
    public static TimeSpan DurationOf(int byteCount, int sampleRate = PcmAudio.SpeechSampleRate) =>
        TimeSpan.FromSeconds((double)(byteCount / BytesPerSample) / sampleRate);
}
