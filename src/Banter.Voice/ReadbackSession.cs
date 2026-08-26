using System.Threading.Channels;
using Bantz.Speech;

namespace Banter.Voice;

/// <summary>
/// Where synthesized audio goes. Separate from <see cref="ITextToSpeech"/> because synthesis is a
/// network call and playback is a device, and the heads that have one do not all have the other
/// in the same form.
/// </summary>
public interface IAudioPlayback
{
    /// <summary>
    /// Plays a buffer, returning when it has been played rather than when it has been queued.
    /// That is what lets the readback queue pace itself: an implementation that returns early
    /// leaves the queue racing ahead and sentences overlapping.
    /// </summary>
    ValueTask PlayAsync(PcmAudio audio, CancellationToken cancellationToken = default);

    /// <summary>Stops at once and drops anything already handed over.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public sealed record ReadbackOptions
{
    public static ReadbackOptions Default { get; } = new();

    public ReadbackPolicy Policy { get; init; } = ReadbackPolicy.AgentsOnly;

    public SentenceSegmenterOptions Sentences { get; init; } = SentenceSegmenterOptions.Default;

    /// <summary>
    /// Sentences allowed to queue. A room where three agents answer at once produces speech
    /// faster than anybody can listen to it; past this the room is better served by dropping the
    /// backlog than by reading it out several minutes late.
    /// </summary>
    public int MaxQueuedSentences { get; init; } = 32;
}

/// <summary>
/// Reads a room aloud (PLAN §6). Messages become sentences, sentences become synthesis requests,
/// and one worker plays them one after another — speech that overlaps is speech nobody can
/// follow, so the queue is serial by design rather than by accident.
/// </summary>
public sealed class ReadbackSession : IAsyncDisposable
{
    private readonly ITextToSpeech _tts;
    private readonly IAudioPlayback _playback;
    private readonly VoiceAssignment _voices;
    private readonly ReadbackOptions _options;

    private readonly Channel<(string Text, string? Voice)> _queue;
    private readonly Dictionary<string, SentenceSegmenter> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;

    private CancellationTokenSource? _current;

    public ReadbackSession(
        ITextToSpeech tts,
        IAudioPlayback playback,
        VoiceAssignment voices,
        ReadbackOptions? options = null)
    {
        _tts = tts;
        _playback = playback;
        _voices = voices;
        _options = options ?? ReadbackOptions.Default;
        Policy = _options.Policy;

        _queue = Channel.CreateBounded<(string, string?)>(new BoundedChannelOptions(_options.MaxQueuedSentences)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _worker = Task.Run(SpeakLoopAsync);
    }

    /// <summary>
    /// Whose messages are read aloud. Settable because §6 makes this a per-room setting and a
    /// head switching rooms has to follow it.
    /// </summary>
    public ReadbackPolicy Policy { get; set; }

    /// <summary>Whether something is being spoken right now.</summary>
    public bool IsSpeaking => Volatile.Read(ref _current) is not null;

    public event Action<VoiceSessionError>? Failed;

    /// <summary>Speaks a complete message, if the policy says it should be spoken.</summary>
    public void Speak(string sender, string text, bool senderIsAgent, bool senderIsSelf)
    {
        if (!Readback.ShouldSpeak(Policy, senderIsAgent, senderIsSelf))
        {
            return;
        }

        // Segmented even though the whole message is in hand: it lets playback start on the first
        // sentence, and it gives a barge-in somewhere to cut in.
        var segmenter = new SentenceSegmenter(_options.Sentences);
        foreach (var sentence in segmenter.Append(text))
        {
            Enqueue(sender, sentence);
        }

        if (segmenter.Flush() is { } rest)
        {
            Enqueue(sender, rest);
        }
    }

    /// <summary>
    /// Adds a delta from a message still streaming, speaking each sentence as it completes.
    /// Nothing is spoken until one does, so a half-finished clause is never read out.
    /// </summary>
    public void AppendDelta(string sender, string delta, bool senderIsAgent, bool senderIsSelf)
    {
        if (!Readback.ShouldSpeak(Policy, senderIsAgent, senderIsSelf))
        {
            return;
        }

        if (!_streams.TryGetValue(sender, out var segmenter))
        {
            _streams[sender] = segmenter = new SentenceSegmenter(_options.Sentences);
        }

        foreach (var sentence in segmenter.Append(delta))
        {
            Enqueue(sender, sentence);
        }
    }

    /// <summary>Ends a stream, speaking whatever was left after the last terminator.</summary>
    public void EndStream(string sender)
    {
        if (!_streams.Remove(sender, out var segmenter))
        {
            return;
        }

        if (segmenter.Flush() is { } rest)
        {
            Enqueue(sender, rest);
        }
    }

    /// <summary>
    /// Stops speaking and drops everything queued.
    ///
    /// <para>This is barge-in: the moment the user starts talking, the room should stop talking
    /// over them. It is also what a room switch and the mute switch call, since neither wants the
    /// previous room's backlog read out.</para>
    /// </summary>
    public async ValueTask SilenceAsync(CancellationToken cancellationToken = default)
    {
        while (_queue.Reader.TryRead(out _))
        {
            // Drained, not cancelled: the queue outlives any one utterance.
        }

        _streams.Clear();

        try
        {
            Volatile.Read(ref _current)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker finished that utterance between the read and the cancel. Nothing to stop.
        }

        await _playback.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Enqueue(string sender, string sentence)
    {
        if (!_queue.Writer.TryWrite((sentence, _voices.For(sender))))
        {
            Failed?.Invoke(new VoiceSessionError(
                "The room is being read aloud slower than it is being written; a sentence was dropped."));
        }
    }

    private async Task SpeakLoopAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    await SpeakOneAsync(item).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
    }

    private async Task SpeakOneAsync((string Text, string? Voice) item)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        Interlocked.Exchange(ref _current, cts)?.Dispose();

        try
        {
            var request = new SpeechRequest(item.Text, item.Voice);
            await foreach (var chunk in _tts.SynthesizeAsync(request, cts.Token).ConfigureAwait(false))
            {
                await _playback.PlayAsync(chunk, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Silenced, or shutting down. Either way this sentence is not wanted any more.
        }
        catch (Exception e)
        {
            // A speech server that refused one sentence must not take the room's audio with it.
            Failed?.Invoke(new VoiceSessionError("Speaking a message failed.", e));
        }
        finally
        {
            Interlocked.CompareExchange(ref _current, null, cts);
            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
            // A speech backend that ignores cancellation must not hold a head's shutdown open.
        }

        _stopping.Dispose();
    }
}
