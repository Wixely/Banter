using Bantz.Speech;
using Xunit;
using static Banter.Voice.Tests.Pcm;

namespace Banter.Voice.Tests;

/// <summary>
/// The pipeline of PLAN §6 end to end, with a scripted microphone and a scripted engine. What is
/// under test is the plumbing between them: which audio becomes an utterance, what order
/// utterances come back in, and what a failure does to the microphone.
/// </summary>
public sealed class VoiceSessionTests
{
    private static (VoiceSession Session, FakeCapture Mic, FakeEngine Engine, List<VoiceDraft> Drafts,
        List<VoiceSessionError> Errors) Build(VoiceCaptureMode mode = VoiceCaptureMode.PushToTalk,
        VoiceSessionOptions? options = null)
    {
        var mic = new FakeCapture();
        var engine = new FakeEngine();
        var session = new VoiceSession(mic, engine, (options ?? VoiceSessionOptions.Default) with { Mode = mode });

        var drafts = new List<VoiceDraft>();
        var errors = new List<VoiceSessionError>();
        session.DraftReady += d => { lock (drafts) { drafts.Add(d); } };
        session.Failed += e => { lock (errors) { errors.Add(e); } };
        return (session, mic, engine, drafts, errors);
    }

    private static int Count<T>(List<T> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    [Fact]
    public async Task APressThatCapturedSpeechProducesADraft()
    {
        var (session, mic, engine, drafts, _) = Build();
        engine.Respond = (_, _) => Task.FromResult(new TranscriptionResult("open the task board", "en"));

        await using (session)
        {
            await session.StartAsync();
            mic.Emit(Concat(Quiet(Ms(300)), Speaking(Ms(900)), Quiet(Ms(300))));
            await session.StopAsync();

            await Wait.UntilAsync(() => Count(drafts) == 1, "the draft");
        }

        Assert.Equal("open the task board", drafts[0].Text);
        Assert.Equal("en", drafts[0].Language);
    }

    [Fact]
    public async Task APressThatCapturedOnlySilenceNeverReachesTheEngine()
    {
        var (session, mic, engine, drafts, _) = Build();

        await using (session)
        {
            await session.StartAsync();
            mic.Emit(Quiet(Ms(1500)));
            await session.StopAsync();
            await Task.Delay(100);
        }

        // The round trip is not the point — a near-silent clip comes back as a confident invented
        // sentence, and that would land in the room under the user's name.
        Assert.Equal(0, engine.Calls);
        Assert.Empty(drafts);
    }

    [Fact]
    public async Task OnlyTheSpeechInAPressIsSentNotTheWholePress()
    {
        var (session, mic, engine, drafts, _) = Build();

        await using (session)
        {
            await session.StartAsync();
            mic.Emit(Concat(Quiet(Ms(2000)), Speaking(Ms(500)), Quiet(Ms(2000))));
            await session.StopAsync();
            await Wait.UntilAsync(() => Count(drafts) == 1, "the draft");
        }

        // Four and a half seconds pressed, half a second said: the rest is upload and latency
        // paid for nothing.
        Assert.True(engine.Durations[0] < Ms(1200), $"sent {engine.Durations[0]} of audio");
    }

    [Fact]
    public async Task AlwaysListeningDeliversAnUtteranceWithoutBeingStopped()
    {
        var (session, mic, _, drafts, _) = Build(VoiceCaptureMode.AlwaysListening);

        await using (session)
        {
            await session.StartAsync();
            mic.Emit(Concat(Speaking(Ms(800)), Quiet(Ms(1200))));

            await Wait.UntilAsync(() => Count(drafts) == 1, "the utterance");
            Assert.True(mic.Running, "the microphone should still be open");
        }
    }

    [Fact]
    public async Task SentencesArriveInTheOrderTheyWereSpokenEvenWhenTheFirstIsSlower()
    {
        var (session, mic, engine, drafts, _) = Build(VoiceCaptureMode.AlwaysListening);
        engine.Respond = async (i, _) =>
        {
            await Task.Delay(i == 0 ? 300 : 1);
            return new TranscriptionResult(i == 0 ? "first" : "second");
        };

        await using (session)
        {
            await session.StartAsync();
            mic.Emit(Concat(
                Speaking(Ms(700)), Quiet(Ms(1200)),
                Speaking(Ms(700)), Quiet(Ms(1200))));

            await Wait.UntilAsync(() => Count(drafts) == 2, "both utterances");
        }

        // Transcribing concurrently would return these the other way round, and a room reading a
        // conversation backwards is worse than one reading it late.
        Assert.Equal(["first", "second"], drafts.Select(d => d.Text));
    }

    [Fact]
    public async Task AnEngineFailureIsReportedAndTheMicrophoneStaysOpen()
    {
        var (session, mic, _, drafts, errors) = Build(VoiceCaptureMode.AlwaysListening);
        var engine = new FakeEngine();

        await using var live = new VoiceSession(mic, engine,
            VoiceSessionOptions.Default with { Mode = VoiceCaptureMode.AlwaysListening });
        var seen = new List<VoiceDraft>();
        var failures = new List<VoiceSessionError>();
        live.DraftReady += d => { lock (seen) { seen.Add(d); } };
        live.Failed += e => { lock (failures) { failures.Add(e); } };

        engine.Respond = (i, _) => i == 0
            ? Task.FromException<TranscriptionResult>(new HttpRequestException("503 from the speech server"))
            : Task.FromResult(new TranscriptionResult("recovered"));

        await live.StartAsync();
        mic.Emit(Concat(Speaking(Ms(700)), Quiet(Ms(1200))));
        await Wait.UntilAsync(() => Count(failures) == 1, "the failure");

        mic.Emit(Concat(Speaking(Ms(700)), Quiet(Ms(1200))));
        await Wait.UntilAsync(() => Count(seen) == 1, "the next utterance");

        // One bad round trip must not take the microphone down with it.
        Assert.Contains("503", failures[0].Cause!.Message);
        Assert.Equal("recovered", seen[0].Text);
        Assert.True(mic.Running);

        await session.DisposeAsync();
        Assert.Empty(drafts);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task AnEmptyTranscriptIsNotADraft()
    {
        var (session, mic, engine, drafts, _) = Build();
        engine.Respond = (_, _) => Task.FromResult(new TranscriptionResult("   "));

        await using (session)
        {
            await session.StartAsync();
            mic.Emit(Speaking(Ms(900)));
            await session.StopAsync();
            await Wait.UntilAsync(() => engine.Calls == 1, "the engine call");
            await Task.Delay(50);
        }

        Assert.Empty(drafts);
    }

    [Fact]
    public async Task AKeyNobodyReleasedIsCappedAndSaidSoAbout()
    {
        var options = VoiceSessionOptions.Default with { MaxPressDuration = Ms(500) };
        var (session, mic, engine, drafts, errors) = Build(VoiceCaptureMode.PushToTalk, options);

        await using (session)
        {
            await session.StartAsync();
            mic.Emit(Speaking(Ms(3000)));
            await session.StopAsync();
            await Wait.UntilAsync(() => Count(drafts) == 1, "the draft");
        }

        // Without the cap a swallowed key-up is a buffer that grows until the process dies.
        Assert.True(engine.Durations[0] <= Ms(600), $"kept {engine.Durations[0]}");
        Assert.Contains(errors, e => e.Message.Contains("held past"));
    }

    [Fact]
    public async Task AudioIsDroppedRatherThanQueuedWithoutLimit()
    {
        var release = new TaskCompletionSource();
        var options = VoiceSessionOptions.Default with { MaxPendingUtterances = 1 };
        var (session, mic, engine, _, errors) = Build(VoiceCaptureMode.AlwaysListening, options);
        engine.Respond = async (_, _) =>
        {
            await release.Task;
            return new TranscriptionResult("eventually");
        };

        await using (session)
        {
            await session.StartAsync();

            // Four utterances against an engine that has not answered the first: one is in the
            // worker, one fits the queue, the rest have nowhere to go.
            for (var i = 0; i < 4; i++)
            {
                mic.Emit(Concat(Speaking(Ms(700)), Quiet(Ms(1200))));
            }

            await Wait.UntilAsync(() => Count(errors) > 0, "the drop");
            release.SetResult();
        }

        Assert.Contains(errors, e => e.Message.Contains("behind"));
    }

    [Fact]
    public async Task FramesArrivingAfterAStopAreIgnored()
    {
        var (session, mic, engine, _, _) = Build(VoiceCaptureMode.AlwaysListening);

        await using (session)
        {
            await session.StartAsync();
            await session.StopAsync();

            // A backend that raised one more frame on the way out must not restart the pipeline.
            mic.Emit(Concat(Speaking(Ms(900)), Quiet(Ms(1200))));
            await Task.Delay(100);
        }

        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task TheIndicatorFollowsWhatTheSessionIsDoing()
    {
        var (session, mic, _, _, _) = Build(VoiceCaptureMode.AlwaysListening);

        await using (session)
        {
            Assert.Equal(VoiceSessionState.Idle, session.State);

            await session.StartAsync();
            Assert.Equal(VoiceSessionState.Listening, session.State);

            mic.Emit(Speaking(Ms(400)));
            Assert.Equal(VoiceSessionState.Capturing, session.State);

            mic.Emit(Quiet(Ms(1200)));
            Assert.Equal(VoiceSessionState.Listening, session.State);

            await session.StopAsync();
            await Wait.UntilAsync(() => session.State == VoiceSessionState.Idle, "the session to settle");
        }
    }

    [Fact]
    public async Task PreparingTheEngineGoesThroughTheInterface()
    {
        var (session, _, _, _, _) = Build();

        // FakeEngine implements only TranscribeAsync; the rest are the interface's own defaults,
        // so this passing is the check that the session leans on the contract and not a class.
        await using (session)
        {
            await session.PrepareAsync();
        }
    }
}
