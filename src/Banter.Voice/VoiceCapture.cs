namespace Banter.Voice;

/// <summary>
/// Microphone frames as they are captured, and nothing more.
///
/// <para>Deliberately not <c>Bantz.Capture.IAudioRecorder</c>, whose <c>StopAsync</c> is
/// documented to return <i>the complete normalized buffer</i>. That contract obliges every
/// implementation to retain the whole recording, which is right for the press-and-release
/// dictation Bantz does and wrong for the always-listening mode of PLAN §6: 16 kHz mono is about
/// 115 MB an hour, and a room microphone is meant to sit on a desk all day. Here nothing is
/// retained — what to keep is the caller's decision, and both capture modes bound it
/// (a press, or one utterance).</para>
///
/// <para>An implementation over a Bantz recorder is a few lines in a head; keeping the seam here
/// also keeps <c>Banter.Voice</c> free of a capture backend's native dependencies, which matters
/// on the heads that will never have a microphone driver of that shape.</para>
/// </summary>
public interface IVoiceCapture
{
    int SampleRate { get; }

    int Channels { get; }

    /// <summary>
    /// Raised for each frame while capturing, on whatever thread the backend uses. Handlers run
    /// on the capture path, so they must not block: the audio pipeline's single-writer discipline
    /// depends on this being one serialised caller.
    /// </summary>
    event Action<ReadOnlyMemory<byte>>? FrameCaptured;

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops capturing. Must not return until no further <see cref="FrameCaptured"/> will be
    /// raised — callers rely on that to hand the last of the audio on without racing the backend.
    /// </summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
