using Banter.Voice;
using Bantz.Capture;
using Bantz.Speech;

namespace Banter.App.Desktop;

/// <summary>
/// A microphone, over the recorders Bantz already ships (PLAN §6a) — Windows wave-in and Linux
/// ALSA, both normalising to the 16 kHz mono the speech engines take.
///
/// <para>Only the frame stream is used. <c>IAudioRecorder.StopAsync</c> also returns the complete
/// recording, and that buffer is discarded here: what to keep is the pipeline's decision, and it
/// keeps a press or a single utterance rather than everything since the microphone opened.</para>
///
/// <para><b>Known cost.</b> The recorder still accumulates that buffer internally whether or not
/// anyone reads it, so an always-listening session grows by about 115 MB an hour until it is
/// stopped. Bounded and harmless for push-to-talk, which is what Phase 3 needs. Raised upstream as
/// a request for a capture mode that retains nothing; when that lands this class loses a paragraph
/// and nothing else.</para>
/// </summary>
public sealed class RecorderVoiceCapture(IAudioRecorder recorder) : IVoiceCapture, IDisposable
{
    public int SampleRate => PcmAudio.SpeechSampleRate;

    public int Channels => PcmAudio.SpeechChannels;

    public event Action<ReadOnlyMemory<byte>>? FrameCaptured;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        recorder.FrameCaptured += Forward;
        return recorder.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Stops, having first detached the handler — <see cref="IVoiceCapture"/> promises no frame
    /// arrives after this returns, and that promise is what lets the pipeline read its buffers
    /// without racing the device.
    /// </summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        recorder.FrameCaptured -= Forward;

        // The returned buffer is deliberately unused; see the note on this class. It cannot be
        // discarded into `_` because Program.cs is top-level statements and owns that name.
        await recorder.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Forward(AudioFrame frame) => FrameCaptured?.Invoke(frame.Audio.Data);

    public void Dispose()
    {
        recorder.FrameCaptured -= Forward;
        (recorder as IDisposable)?.Dispose();
    }
}
