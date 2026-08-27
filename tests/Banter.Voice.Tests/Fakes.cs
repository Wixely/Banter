using Bantz.Speech;

namespace Banter.Voice.Tests;

/// <summary>A microphone the test drives frame by frame.</summary>
internal sealed class FakeCapture : IVoiceCapture
{
    public int SampleRate => PcmAudio.SpeechSampleRate;

    public int Channels => PcmAudio.SpeechChannels;

    public bool Running { get; private set; }

    public event Action<ReadOnlyMemory<byte>>? FrameCaptured;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        Running = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Running = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>Delivers audio the way a backend does. Split, because a real one never hands over
    /// a whole utterance at once.</summary>
    public void Emit(byte[] pcm, int frameBytes = 640)
    {
        for (var offset = 0; offset < pcm.Length; offset += frameBytes)
        {
            FrameCaptured?.Invoke(pcm.AsMemory(offset, Math.Min(frameBytes, pcm.Length - offset)));
        }
    }
}

/// <summary>
/// A transcription engine under the test's control. Only <c>TranscribeAsync</c> is implemented:
/// the rest of <see cref="ITranscriptionEngine"/> is default interface methods.
/// </summary>
internal sealed class FakeEngine : ITranscriptionEngine
{
    private int _calls;

    /// <summary>Answers by call index, so a test can vary text, delay, or failure per utterance.</summary>
    public Func<int, PcmAudio, Task<TranscriptionResult>> Respond { get; set; } =
        (i, _) => Task.FromResult(new TranscriptionResult($"utterance {i}"));

    public List<TimeSpan> Durations { get; } = [];

    public int Calls => Volatile.Read(ref _calls);

    public async Task<TranscriptionResult> TranscribeAsync(
        PcmAudio audio,
        CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _calls) - 1;
        lock (Durations)
        {
            Durations.Add(audio.Duration);
        }

        return await Respond(index, audio);
    }
}

internal static class Wait
{
    /// <summary>
    /// Spins until <paramref name="condition"/> holds. The pipeline crosses a thread on purpose,
    /// so a test that asserts straight after emitting audio is asserting on a race.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int millisecondsTimeout = 5000)
    {
        var deadline = Environment.TickCount64 + millisecondsTimeout;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }
}

/// <summary>
/// Reports on the calling thread. <see cref="Progress{T}"/> posts its callback to the thread pool
/// instead, so a test asserting straight after an await is racing it — which passes alone and
/// fails when the whole solution runs.
/// </summary>
internal sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
