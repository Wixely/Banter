using System.Diagnostics;
using System.Runtime.InteropServices;
using Banter.Voice;
using Bantz.Capture;
using Bantz.Speech;
using NAudio.Wave;

namespace Banter.App.Desktop;

/// <summary>
/// Picks the audio devices for this machine. Everything here returns <see langword="null"/> rather
/// than throwing when a platform cannot oblige — the app then hides its voice controls instead of
/// offering a button that fails on the first press.
/// </summary>
public static class DesktopAudio
{
    public static IVoiceCapture? TryCreateCapture()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return new RecorderVoiceCapture(new WindowsAudioRecorder());
            }

            if (OperatingSystem.IsLinux())
            {
                return new RecorderVoiceCapture(new LinuxAudioRecorder());
            }
        }
        catch (Exception)
        {
            // No device, no driver, or a headless session. Not having a microphone is a normal
            // state for a chat client, not an error worth failing to start over.
        }

        return null;
    }

    public static IAudioPlayback? TryCreatePlayback()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsAudioPlayback();
            }

            if (OperatingSystem.IsLinux())
            {
                return new AplayAudioPlayback();
            }
        }
        catch (Exception)
        {
            // As above: silence is survivable.
        }

        return null;
    }
}

/// <summary>
/// Playback through NAudio's wave-out. One device is kept open and fed, rather than one per
/// sentence: opening a device costs tens of milliseconds and clicks audibly on each open.
/// </summary>
public sealed class WindowsAudioPlayback : IAudioPlayback, IDisposable
{
    /// <summary>
    /// How much audio may still be queued when <see cref="PlayAsync"/> returns. The contract is
    /// "returns when played", and waiting for the buffer to hit exactly zero would stall between
    /// every chunk of one sentence; a small tail keeps playback continuous while still pacing the
    /// queue behind it.
    /// </summary>
    private static readonly TimeSpan Watermark = TimeSpan.FromMilliseconds(60);

    private readonly Lock _gate = new();
    private WaveOutEvent? _device;
    private BufferedWaveProvider? _buffer;
    private int _rate;
    private int _channels;

    public async ValueTask PlayAsync(PcmAudio audio, CancellationToken cancellationToken = default)
    {
        if (audio.Data.Length == 0)
        {
            return;
        }

        BufferedWaveProvider buffer;
        lock (_gate)
        {
            buffer = Ensure(audio.SampleRate, audio.Channels);
            buffer.AddSamples(audio.Data.ToArray(), 0, audio.Data.Length);
        }

        while (!cancellationToken.IsCancellationRequested && buffer.BufferedDuration > Watermark)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Clearing the buffer is what makes a barge-in immediate: stopping the device alone
            // would leave the rest of the sentence to play the moment the next one started.
            _buffer?.ClearBuffer();
            _device?.Stop();
        }

        return ValueTask.CompletedTask;
    }

    private BufferedWaveProvider Ensure(int rate, int channels)
    {
        if (_buffer is not null && _rate == rate && _channels == channels)
        {
            if (_device!.PlaybackState != PlaybackState.Playing)
            {
                _device.Play();
            }

            return _buffer;
        }

        // A synthesis at a different rate needs its own device; NAudio's format is fixed at open.
        _device?.Dispose();
        _rate = rate;
        _channels = channels;
        _buffer = new BufferedWaveProvider(new NAudio.Wave.WaveFormat(rate, 16, channels))
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(10),
        };

        _device = new WaveOutEvent();
        _device.Init(_buffer);
        _device.Play();
        return _buffer;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _device?.Dispose();
            _device = null;
            _buffer = null;
        }
    }
}

/// <summary>
/// Playback on Linux by piping raw samples to <c>aplay</c> — the same shape Bantz's Linux capture
/// uses for <c>arecord</c>, and the reason neither needs a native binding.
///
/// <para>Pacing comes free: <c>aplay</c> stops reading its stdin when its own buffer is full, so
/// the write blocks for exactly as long as the audio takes to play.</para>
/// </summary>
public sealed class AplayAudioPlayback : IAudioPlayback, IDisposable
{
    private readonly Lock _gate = new();
    private Process? _aplay;
    private int _rate;
    private int _channels;

    public async ValueTask PlayAsync(PcmAudio audio, CancellationToken cancellationToken = default)
    {
        if (audio.Data.Length == 0)
        {
            return;
        }

        Process process;
        lock (_gate)
        {
            process = Ensure(audio.SampleRate, audio.Channels);
        }

        try
        {
            await process.StandardInput.BaseStream
                .WriteAsync(audio.Data, cancellationToken)
                .ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // aplay exited — a device taken by something else, or killed by StopAsync mid-write.
            // Dropped rather than thrown: the readback queue survives a lost sentence.
            lock (_gate)
            {
                Kill();
            }
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // No way to flush aplay's buffer, so a barge-in ends the process. The next sentence
            // starts a new one, which costs a process launch and is inaudible next to the wait.
            Kill();
        }

        return ValueTask.CompletedTask;
    }

    private Process Ensure(int rate, int channels)
    {
        if (_aplay is { HasExited: false } && _rate == rate && _channels == channels)
        {
            return _aplay;
        }

        Kill();
        _rate = rate;
        _channels = channels;

        var start = new ProcessStartInfo("aplay")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in new[] { "-q", "-t", "raw", "-f", "S16_LE", "-r", $"{rate}", "-c", $"{channels}" })
        {
            start.ArgumentList.Add(argument);
        }

        _aplay = Process.Start(start) ?? throw new InvalidOperationException("aplay did not start.");
        return _aplay;
    }

    private void Kill()
    {
        if (_aplay is null)
        {
            return;
        }

        try
        {
            if (!_aplay.HasExited)
            {
                _aplay.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _aplay.Dispose();
        _aplay = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Kill();
        }
    }
}
