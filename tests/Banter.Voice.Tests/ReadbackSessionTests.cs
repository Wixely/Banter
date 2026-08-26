using System.Runtime.CompilerServices;
using Bantz.Speech;
using Xunit;
using static Banter.Voice.Tests.Pcm;

namespace Banter.Voice.Tests;

/// <summary>A speech backend the test drives, recording what it was asked to say.</summary>
internal sealed class FakeTts : ITextToSpeech
{
    public List<SpeechRequest> Spoken { get; } = [];

    public Func<SpeechRequest, Task>? Before { get; set; }

    public bool IsReady => true;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask<IReadOnlyList<VoiceDescriptor>> GetVoicesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<VoiceDescriptor>>([new("alloy"), new("echo")]);

    public async IAsyncEnumerable<PcmAudio> SynthesizeAsync(
        SpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (Spoken)
        {
            Spoken.Add(request);
        }

        if (Before is { } before)
        {
            await before(request);
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new PcmAudio(Speaking(Ms(100)), 24000);
    }

    public List<SpeechRequest> Snapshot()
    {
        lock (Spoken)
        {
            return [.. Spoken];
        }
    }
}

internal sealed class FakePlayback : IAudioPlayback
{
    public List<PcmAudio> Played { get; } = [];

    public int Stops { get; private set; }

    public ValueTask PlayAsync(PcmAudio audio, CancellationToken cancellationToken = default)
    {
        lock (Played)
        {
            Played.Add(audio);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Stops++;
        return ValueTask.CompletedTask;
    }

    public int Count
    {
        get
        {
            lock (Played)
            {
                return Played.Count;
            }
        }
    }
}

/// <summary>
/// The room being read aloud. What matters is order, what gets spoken at all, and that a barge-in
/// actually stops the speaking rather than merely stopping the queue.
/// </summary>
public sealed class ReadbackSessionTests
{
    private static (ReadbackSession Session, FakeTts Tts, FakePlayback Playback) Build(
        ReadbackOptions? options = null,
        IReadOnlyList<VoiceDescriptor>? pool = null)
    {
        var tts = new FakeTts();
        var playback = new FakePlayback();
        var voices = new VoiceAssignment(pool ?? [new("alloy"), new("echo"), new("fable")]);
        return (new ReadbackSession(tts, playback, voices, options), tts, playback);
    }

    [Fact]
    public async Task AnAgentsMessageIsSpokenSentenceBySentence()
    {
        var (session, tts, _) = Build();

        await using (session)
        {
            session.Speak("dagger", "The board has three tasks. Two are claimed.", true, false);
            await Wait.UntilAsync(() => tts.Snapshot().Count == 2, "both sentences");
        }

        Assert.Equal(
            ["The board has three tasks.", "Two are claimed."],
            tts.Snapshot().Select(r => r.Text));
    }

    [Fact]
    public async Task SentencesAreSpokenInOrderAndNeverOverlap()
    {
        var (session, tts, _) = Build();
        var speaking = 0;
        var overlapped = false;
        tts.Before = async _ =>
        {
            if (Interlocked.Increment(ref speaking) > 1)
            {
                overlapped = true;
            }

            await Task.Delay(20);
            Interlocked.Decrement(ref speaking);
        };

        await using (session)
        {
            session.Speak("dagger", "One. Two. Three.", true, false);
            await Wait.UntilAsync(() => tts.Snapshot().Count == 3, "all three sentences");
        }

        // Speech that overlaps is speech nobody can follow.
        Assert.False(overlapped, "two sentences were synthesised at once");
        Assert.Equal(["One.", "Two.", "Three."], tts.Snapshot().Select(r => r.Text));
    }

    [Fact]
    public async Task ThePolicyIsHonoured()
    {
        var (session, tts, _) = Build(ReadbackOptions.Default with { Policy = ReadbackPolicy.AgentsOnly });

        await using (session)
        {
            session.Speak("bob", "a human speaking. ", senderIsAgent: false, senderIsSelf: false);
            session.Speak("dagger", "an agent speaking. ", senderIsAgent: true, senderIsSelf: false);
            await Wait.UntilAsync(() => tts.Snapshot().Count == 1, "the agent's message");
            await Task.Delay(50);
        }

        Assert.Equal(["an agent speaking."], tts.Snapshot().Select(r => r.Text));
    }

    [Fact]
    public async Task YourOwnMessagesAreNotReadBackToYou()
    {
        var (session, tts, _) = Build(ReadbackOptions.Default with { Policy = ReadbackPolicy.Everyone });

        await using (session)
        {
            session.Speak("alice", "what I just said. ", senderIsAgent: false, senderIsSelf: true);
            await Task.Delay(100);
        }

        // Otherwise always-listening hears it, transcribes it, sends it, and hears it again.
        Assert.Empty(tts.Snapshot());
    }

    [Fact]
    public async Task EachSenderIsSpokenInTheirOwnVoice()
    {
        var (session, tts, _) = Build();

        await using (session)
        {
            session.Speak("dagger", "first. ", true, false);
            session.Speak("warden", "second. ", true, false);
            await Wait.UntilAsync(() => tts.Snapshot().Count == 2, "both messages");
        }

        var byVoice = tts.Snapshot().Select(r => r.Voice).ToList();
        Assert.Equal(2, byVoice.Distinct().Count());
        Assert.DoesNotContain(byVoice, v => v is null);
    }

    [Fact]
    public async Task AStreamIsSpokenAsItsSentencesComplete()
    {
        var (session, tts, _) = Build();

        await using (session)
        {
            session.AppendDelta("dagger", "I checked the ", true, false);
            await Task.Delay(30);
            Assert.Empty(tts.Snapshot());               // half a clause is not worth speaking

            session.AppendDelta("dagger", "board. Two are ", true, false);
            await Wait.UntilAsync(() => tts.Snapshot().Count == 1, "the first sentence");

            session.AppendDelta("dagger", "claimed", true, false);
            session.EndStream("dagger");
            await Wait.UntilAsync(() => tts.Snapshot().Count == 2, "the tail of the stream");
        }

        Assert.Equal(["I checked the board.", "Two are claimed"], tts.Snapshot().Select(r => r.Text));
    }

    [Fact]
    public async Task SilencingStopsThePlaybackDeviceAndDropsTheBacklog()
    {
        var release = new TaskCompletionSource();
        var (session, tts, playback) = Build();
        tts.Before = _ => release.Task;

        await using (session)
        {
            session.Speak("dagger", "One. Two. Three. Four. ", true, false);
            await Wait.UntilAsync(() => tts.Snapshot().Count == 1, "the first sentence to start");

            await session.SilenceAsync();
            release.SetResult();
            await Task.Delay(100);
        }

        // Barge-in has to reach the speaker, not just the queue: stopping the queue while a
        // sentence is already playing leaves the room talking over the user.
        Assert.Equal(1, playback.Stops);
        Assert.True(tts.Snapshot().Count <= 2, $"{tts.Snapshot().Count} sentences survived the barge-in");
        Assert.Equal(0, playback.Count);
    }

    [Fact]
    public async Task SilencingDropsAStreamStillArriving()
    {
        var (session, tts, _) = Build();

        await using (session)
        {
            session.AppendDelta("dagger", "half a thought ", true, false);
            await session.SilenceAsync();
            session.EndStream("dagger");
            await Task.Delay(80);
        }

        Assert.Empty(tts.Snapshot());
    }

    [Fact]
    public async Task ASpeechFailureIsReportedAndTheNextSentenceStillPlays()
    {
        var (session, tts, playback) = Build();
        var failures = new List<VoiceSessionError>();
        session.Failed += e => { lock (failures) { failures.Add(e); } };

        var first = true;
        tts.Before = _ =>
        {
            if (!first)
            {
                return Task.CompletedTask;
            }

            first = false;
            return Task.FromException(new HttpRequestException("503 from the speech server"));
        };

        await using (session)
        {
            session.Speak("dagger", "One. Two. ", true, false);
            await Wait.UntilAsync(() => playback.Count == 1, "the second sentence");
        }

        Assert.Single(failures);
        Assert.Contains("503", failures[0].Cause!.Message);
    }

    [Fact]
    public async Task SynthesizedAudioReachesTheSpeaker()
    {
        var (session, _, playback) = Build();

        await using (session)
        {
            session.Speak("dagger", "hello. ", true, false);
            await Wait.UntilAsync(() => playback.Count == 1, "the audio");
        }

        Assert.Equal(24000, playback.Played[0].SampleRate);
    }

    [Fact]
    public async Task WithNoVoicesTheBackendsDefaultIsUsed()
    {
        var (session, tts, _) = Build(pool: []);

        await using (session)
        {
            session.Speak("dagger", "hello. ", true, false);
            await Wait.UntilAsync(() => tts.Snapshot().Count == 1, "the message");
        }

        // Null asks for the server's default rather than inventing a voice name it will reject.
        Assert.Null(tts.Snapshot()[0].Voice);
    }
}
