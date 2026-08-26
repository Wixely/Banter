using System.Threading.Channels;
using Bantz.Speech;

namespace Banter.Voice;

/// <summary>How the microphone decides what to send (PLAN §6).</summary>
public enum VoiceCaptureMode
{
    /// <summary>Held while speaking; the release is what ends the utterance.</summary>
    PushToTalk,

    /// <summary>Continuous, with the energy gate deciding where utterances begin and end.</summary>
    AlwaysListening,
}

/// <summary>What a session is doing, for the listening indicator the user watches.</summary>
public enum VoiceSessionState
{
    Idle,

    /// <summary>Capturing, gate closed — armed but hearing nothing worth sending.</summary>
    Listening,

    /// <summary>Gate open: a push-to-talk key is held, or a voice is being heard.</summary>
    Capturing,

    /// <summary>At least one utterance is with the engine.</summary>
    Transcribing,
}

/// <summary>Text the user said, ready for the head to review or send.</summary>
public sealed record VoiceDraft(string Text, TimeSpan AudioDuration, string? Language);

/// <summary>Something went wrong that did not end the session.</summary>
public sealed record VoiceSessionError(string Message, Exception? Cause = null);

public sealed record VoiceSessionOptions
{
    public static VoiceSessionOptions Default { get; } = new();

    public VoiceCaptureMode Mode { get; init; } = VoiceCaptureMode.PushToTalk;

    public VoiceActivityOptions Activity { get; init; } = VoiceActivityOptions.Default;

    /// <summary>
    /// A ceiling on one push-to-talk press. A global hotkey can be held by a stuck key or a
    /// window that swallowed the key-up, and without a cap that is a buffer growing until the
    /// process dies.
    /// </summary>
    public TimeSpan MaxPressDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Utterances allowed to queue for transcription. A remote engine slower than the person
    /// talking is a backlog; bounding it drops audio, which is bad, but the alternative is an
    /// unbounded queue delivering sentences minutes after they were said, which is worse and
    /// harder to notice.
    /// </summary>
    public int MaxPendingUtterances { get; init; } = 8;
}

/// <summary>
/// The client audio pipeline of PLAN §6: microphone to draft text, in either capture mode.
///
/// <para>Both modes share everything after the gate, which is why they are one class. What
/// differs is where an utterance comes from — a release in push-to-talk, the gate closing in
/// always-listening — and both end up on the same queue.</para>
///
/// <para>Transcription runs on one worker reading that queue, never on the capture thread. One
/// worker rather than several on purpose: two utterances transcribed concurrently finish in
/// whatever order the engine happens to return them, and sentences arriving in the room backwards
/// is worse than sentences arriving late.</para>
/// </summary>
public sealed class VoiceSession : IAsyncDisposable
{
    /// <summary>How long disposal waits for an in-flight engine call to notice cancellation.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private readonly IVoiceCapture _capture;
    private readonly ITranscriptionEngine _engine;
    private readonly VoiceSessionOptions _options;

    private readonly Channel<PcmAudio> _pending;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;

    private UtteranceSegmenter? _segmenter;
    private MemoryStream? _press;
    private int _pressCap;
    private bool _pressTruncated;
    private bool _running;
    private int _inFlight;
    private VoiceSessionState _state = VoiceSessionState.Idle;

    public VoiceSession(IVoiceCapture capture, ITranscriptionEngine engine, VoiceSessionOptions? options = null)
    {
        _capture = capture;
        _engine = engine;
        _options = options ?? VoiceSessionOptions.Default;

        _pending = Channel.CreateBounded<PcmAudio>(new BoundedChannelOptions(_options.MaxPendingUtterances)
        {
            SingleReader = true,
            SingleWriter = true,

            // Wait, not DropWrite. Under DropWrite a full channel makes TryWrite report success
            // and throw the item away, so the drop below would never be reported and the
            // in-flight count would climb for utterances nobody was ever going to read. Under
            // Wait, TryWrite simply fails when there is no room, which is the answer wanted here.
            FullMode = BoundedChannelFullMode.Wait,
        });

        _capture.FrameCaptured += OnFrame;
        _worker = Task.Run(TranscribeLoopAsync);
    }

    public VoiceSessionState State => _state;

    public event Action<VoiceSessionState>? StateChanged;

    /// <summary>Raised for each transcribed utterance, in the order they were spoken.</summary>
    public event Action<VoiceDraft>? DraftReady;

    /// <summary>
    /// Raised for failures the session survives — an engine that refused one utterance, audio
    /// dropped because the queue was full. Reported rather than thrown because the microphone
    /// staying on through a hiccup is the behaviour that makes sense.
    /// </summary>
    public event Action<VoiceSessionError>? Failed;

    /// <summary>Prepares the engine, reporting whatever it has to download or load.</summary>
    public ValueTask PrepareAsync(
        IProgress<TranscriptionInitializationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _engine.InitializeAsync(progress, cancellationToken);

    /// <summary>
    /// Opens the microphone. In push-to-talk this is the press; in always-listening it arms the
    /// gate and stays open until <see cref="StopAsync"/>.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_running)
        {
            return;
        }

        _running = true;

        if (_options.Mode == VoiceCaptureMode.AlwaysListening)
        {
            _segmenter = new UtteranceSegmenter(_options.Activity, _capture.SampleRate, _capture.Channels);
            _segmenter.UtteranceCompleted += OnUtterance;
        }
        else
        {
            _press = new MemoryStream();
            _pressTruncated = false;
            _pressCap = AudioLevels.BytesFor(_options.MaxPressDuration, _capture.SampleRate) * _capture.Channels;
        }

        SetState(_options.Mode == VoiceCaptureMode.AlwaysListening
            ? VoiceSessionState.Listening
            : VoiceSessionState.Capturing);

        await _capture.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the microphone and hands on whatever was still in flight — the release in
    /// push-to-talk, the last unfinished utterance in always-listening.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_running)
        {
            return;
        }

        _running = false;

        // Capture is stopped first, and IVoiceCapture promises no frame arrives after it returns.
        // That is what makes the buffers below safe to read from this thread.
        await _capture.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_options.Mode == VoiceCaptureMode.AlwaysListening)
        {
            _segmenter?.Flush();
            _segmenter = null;
        }
        else if (_press is { } press)
        {
            _press = null;
            if (_pressTruncated)
            {
                Report(new VoiceSessionError(
                    $"The key was held past {_options.MaxPressDuration.TotalMinutes:0} minutes; " +
                    "only the start of it was kept."));
            }

            var recorded = new PcmAudio(press.ToArray(), _capture.SampleRate, _capture.Channels);
            var trimmed = SpeechTrimmer.Trim(recorded, _options.Activity);
            if (trimmed is not null)
            {
                Enqueue(trimmed);
            }

            await press.DisposeAsync().ConfigureAwait(false);
        }

        SettleState();
    }

    private void OnFrame(ReadOnlyMemory<byte> frame)
    {
        if (!_running)
        {
            return;
        }

        if (_segmenter is { } segmenter)
        {
            var wasOpen = segmenter.IsSpeaking;
            segmenter.Append(frame.Span);
            if (segmenter.IsSpeaking != wasOpen)
            {
                SettleState();
            }

            return;
        }

        if (_press is not { } press)
        {
            return;
        }

        var room = _pressCap - (int)press.Length;
        if (room <= 0)
        {
            // Keep the start rather than the end: with a key nothing released, the beginning is
            // the part somebody meant to say.
            _pressTruncated = true;
            return;
        }

        press.Write(frame.Span[..Math.Min(room, frame.Length)]);
        _pressTruncated |= frame.Length > room;
    }

    private void OnUtterance(PcmAudio audio) => Enqueue(audio);

    private void Enqueue(PcmAudio audio)
    {
        // Counted before it is queued. The worker can read and finish an utterance before this
        // method gets its next line, so counting afterwards lets the decrement land first and
        // drive the count negative.
        Interlocked.Increment(ref _inFlight);

        if (!_pending.Writer.TryWrite(audio))
        {
            Interlocked.Decrement(ref _inFlight);
            Report(new VoiceSessionError(
                "Transcription is behind; an utterance was dropped rather than queued indefinitely."));
            return;
        }

        SettleState();
    }

    private async Task TranscribeLoopAsync()
    {
        try
        {
            while (await _pending.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                while (_pending.Reader.TryRead(out var audio))
                {
                    try
                    {
                        var result = await _engine
                            .TranscribeAsync(audio, _stopping.Token)
                            .ConfigureAwait(false);

                        // Silence still reaches an engine now and then, and what comes back is a
                        // plausible sentence rather than nothing. An empty result is the honest
                        // case and simply is not a draft.
                        if (!string.IsNullOrWhiteSpace(result.Text))
                        {
                            DraftReady?.Invoke(new VoiceDraft(result.Text, audio.Duration, result.Language));
                        }
                    }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        Report(new VoiceSessionError("Transcription failed.", e));
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlight);
                        SettleState();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
    }

    /// <summary>Derives the state from what is actually happening, so no path has to remember to.</summary>
    private void SettleState()
    {
        var next = (_running, _segmenter?.IsSpeaking ?? false, Volatile.Read(ref _inFlight)) switch
        {
            (true, true, _) => VoiceSessionState.Capturing,
            (true, false, _) when _options.Mode == VoiceCaptureMode.PushToTalk => VoiceSessionState.Capturing,
            (true, false, _) => VoiceSessionState.Listening,
            (false, _, > 0) => VoiceSessionState.Transcribing,
            _ => VoiceSessionState.Idle,
        };

        SetState(next);
    }

    private void SetState(VoiceSessionState next)
    {
        if (_state == next)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(next);
    }

    private void Report(VoiceSessionError error) => Failed?.Invoke(error);

    public async ValueTask DisposeAsync()
    {
        _capture.FrameCaptured -= OnFrame;
        _pending.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            // Bounded, because the worker may be inside an engine call. Cancellation is a request,
            // and an engine that does not honour it must not be able to hold disposal — and with
            // it the head shutting down — open indefinitely.
            await _worker.WaitAsync(ShutdownGrace).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the worker is cancelled, not asked politely.
        }
        catch (TimeoutException)
        {
            Report(new VoiceSessionError(
                $"The transcription engine did not return within {ShutdownGrace.TotalSeconds:0} s of being " +
                "cancelled; the session was closed anyway."));
        }

        _stopping.Dispose();
        _press?.Dispose();
    }
}
