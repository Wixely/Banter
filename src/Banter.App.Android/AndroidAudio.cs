using Android.Media;
using Banter.Voice;
using Bantz.Speech;
using Stream = Android.Media.Stream;

namespace Banter.App.Android;

/// <summary>
/// The phone's microphone, as <see cref="IVoiceCapture"/> — 16 kHz mono signed 16-bit, which is
/// what the speech engines take and what <c>AudioRecord</c> is guaranteed to provide.
///
/// <para>Reading is a blocking call on a thread of its own. <c>AudioRecord.Read</c> waits for the
/// hardware, so doing it on the UI or GL thread would stall the frame for as long as the buffer
/// takes to fill.</para>
/// </summary>
public sealed class AndroidVoiceCapture : IVoiceCapture, IDisposable
{
    private readonly Lock _gate = new();
    private AudioRecord? _record;
    private CancellationTokenSource? _stopping;
    private Task? _reader;

    public int SampleRate => PcmAudio.SpeechSampleRate;

    public int Channels => PcmAudio.SpeechChannels;

    public event Action<ReadOnlyMemory<byte>>? FrameCaptured;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_record is not null)
            {
                return ValueTask.CompletedTask;
            }

            var minimum = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
            if (minimum <= 0)
            {
                throw new InvalidOperationException("This device refused a 16 kHz mono recording buffer.");
            }

            // Four times the minimum: the minimum is the point at which the buffer overruns if the
            // reader is even slightly late, and a dropped frame is a clipped word.
            var buffer = minimum * 4;

            var record = new AudioRecord(AudioSource.Mic, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, buffer);
            if (record.State != State.Initialized)
            {
                record.Release();
                throw new InvalidOperationException(
                    "The microphone could not be opened; another app may be holding it.");
            }

            record.StartRecording();
            _record = record;
            _stopping = new CancellationTokenSource();
            _reader = Task.Run(() => ReadLoop(record, buffer, _stopping.Token));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Stops, and does not return until the reader has finished — <see cref="IVoiceCapture"/>
    /// promises no frame arrives after this, and the pipeline reads its buffers on the strength
    /// of that promise.
    /// </summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        AudioRecord? record;
        Task? reader;
        CancellationTokenSource? stopping;

        lock (_gate)
        {
            record = _record;
            reader = _reader;
            stopping = _stopping;
            _record = null;
            _reader = null;
            _stopping = null;
        }

        if (record is null)
        {
            return;
        }

        if (stopping is not null)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        if (reader is not null)
        {
            await reader.ConfigureAwait(false);
        }

        try
        {
            record.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Already stopped by the platform — the device was taken, or the app was backgrounded.
        }

        record.Release();
        record.Dispose();
        stopping?.Dispose();
    }

    private void ReadLoop(AudioRecord record, int bufferBytes, CancellationToken cancellationToken)
    {
        // A fifth of the buffer per read: small enough that the energy gate sees speech begin
        // promptly, large enough not to wake the thread constantly.
        var frame = new byte[Math.Max(640, bufferBytes / 5)];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = record.Read(frame, 0, frame.Length);
            }
            catch (Exception)
            {
                return;                                     // device taken away mid-read
            }

            if (read <= 0)
            {
                // Negative values are AudioRecord's error codes; either way there is nothing to
                // hand on and the next read will either recover or the token will end this.
                continue;
            }

            FrameCaptured?.Invoke(frame.AsMemory(0, read));
        }
    }

    public void Dispose() => StopAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>
/// The phone's speaker, as <see cref="IAudioPlayback"/>.
///
/// <para><c>PlayAsync</c> returns when the audio has actually been heard rather than when it was
/// handed over, which is what paces the readback queue behind it. <c>AudioTrack.Write</c> alone
/// only fills a buffer, so the playback head is watched instead.</para>
/// </summary>
public sealed class AndroidAudioPlayback : IAudioPlayback, IDisposable
{
    /// <summary>
    /// How much may still be queued when a write is called done. Waiting for the buffer to empty
    /// would stall between the chunks of one sentence and make it stutter.
    /// </summary>
    private static readonly TimeSpan Watermark = TimeSpan.FromMilliseconds(60);

    private readonly Lock _gate = new();
    private AudioTrack? _track;
    private int _rate;
    private int _channels;
    private long _framesWritten;

    public async ValueTask PlayAsync(PcmAudio audio, CancellationToken cancellationToken = default)
    {
        if (audio.Data.Length == 0)
        {
            return;
        }

        AudioTrack track;
        int rate;
        lock (_gate)
        {
            track = Ensure(audio.SampleRate, audio.Channels);
            rate = _rate;
        }

        var bytes = audio.Data.ToArray();
        var written = track.Write(bytes, 0, bytes.Length);
        if (written <= 0)
        {
            return;                                         // track stopped underneath us
        }

        long total;
        lock (_gate)
        {
            _framesWritten += written / (2 * _channels);
            total = _framesWritten;
        }

        var watermarkFrames = (long)(Watermark.TotalSeconds * rate);
        while (!cancellationToken.IsCancellationRequested)
        {
            long played;
            lock (_gate)
            {
                if (_track != track)
                {
                    return;                                 // silenced, and this audio is stale
                }

                // Wraps at 2^32 frames — about 50 hours at 24 kHz — and is unsigned on the
                // platform, so it is widened rather than compared as a signed int.
                played = (uint)track.PlaybackHeadPosition;
            }

            if (total - played <= watermarkFrames)
            {
                return;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_track is not { } track)
            {
                return ValueTask.CompletedTask;
            }

            try
            {
                // Pause then flush, in that order: flushing a playing track is documented to be
                // ignored, which would leave the rest of the sentence to play under the next one.
                track.Pause();
                track.Flush();
            }
            catch (Java.Lang.IllegalStateException)
            {
                // Already gone.
            }

            _framesWritten = 0;
        }

        return ValueTask.CompletedTask;
    }

    private AudioTrack Ensure(int rate, int channels)
    {
        if (_track is { } existing && _rate == rate && _channels == channels)
        {
            if (existing.PlayState != PlayState.Playing)
            {
                existing.Play();
            }

            return existing;
        }

        Release();
        _rate = rate;
        _channels = channels;
        _framesWritten = 0;

        var mask = channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
        var minimum = AudioTrack.GetMinBufferSize(rate, mask, Encoding.Pcm16bit);
        var buffer = Math.Max(minimum * 2, minimum);

        var track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()!
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!)!
            .SetAudioFormat(new AudioFormat.Builder()!
                .SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(rate)!
                .SetChannelMask(mask)!
                .Build()!)!
            .SetBufferSizeInBytes(buffer)!
            .SetTransferMode(AudioTrackMode.Stream)!
            .Build();

        track.Play();
        _track = track;
        return track;
    }

    private void Release()
    {
        if (_track is not { } track)
        {
            return;
        }

        try
        {
            track.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Already stopped.
        }

        track.Release();
        track.Dispose();
        _track = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Release();
        }
    }
}
