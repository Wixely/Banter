using System.Text.Json.Nodes;
using Bantz.Speech;
using Banter.Voice.Wyoming;
using Xunit;
using static Banter.Voice.Tests.Pcm;

namespace Banter.Voice.Tests;

/// <summary>
/// The Wyoming adapters against a service that parses the wire independently of they way they
/// write it (PLAN §6). Framing is the whole risk here: a header line and the audio after it arrive
/// in the same packets, so anything read past the newline has to be kept.
/// </summary>
public sealed class WyomingTranscriptionTests
{
    private static WyomingOptions Options(FakeWyomingServer server) =>
        new() { Host = server.Host, Port = server.Port };

    private static JsonObject Format(int rate = 16000, int width = 2, int channels = 1) =>
        new() { ["rate"] = rate, ["width"] = width, ["channels"] = channels };

    [Fact]
    public async Task TheTranscriptComesBack()
    {
        await using var server = FakeWyomingServer.Start(_ =>
            [WireEvent.Of("transcript", new JsonObject { ["text"] = "  open the task board  " })]);

        var engine = new WyomingTranscriptionEngine(Options(server));
        var result = await engine.TranscribeAsync(Audio(Speaking(Ms(400))));

        Assert.Equal("open the task board", result.Text);
    }

    [Fact]
    public async Task TheAudioArrivesIntactAcrossChunks()
    {
        await using var server = FakeWyomingServer.Start(_ =>
            [WireEvent.Of("transcript", new JsonObject { ["text"] = "x" })]);

        var spoken = Speaking(Ms(2000));
        var options = Options(server) with { ChunkBytes = 1000 };   // not a multiple of anything

        await new WyomingTranscriptionEngine(options).TranscribeAsync(Audio(spoken));

        var chunks = server.Received.Where(e => e.Type == "audio-chunk").ToList();
        Assert.True(chunks.Count > 1, "expected the clip to be split across chunks");
        Assert.Equal(spoken, chunks.SelectMany(c => c.Payload).ToArray());
    }

    [Fact]
    public async Task TheConversationFollowsTheProtocolsOrder()
    {
        await using var server = FakeWyomingServer.Start(_ =>
            [WireEvent.Of("transcript", new JsonObject { ["text"] = "x" })]);

        await new WyomingTranscriptionEngine(Options(server)).TranscribeAsync(Audio(Speaking(Ms(300))));

        var types = server.Received.Select(e => e.Type).ToList();
        Assert.Equal("transcribe", types[0]);
        Assert.Equal("audio-start", types[1]);
        Assert.Equal("audio-stop", types[^1]);
        Assert.All(types[2..^1], t => Assert.Equal("audio-chunk", t));
    }

    [Fact]
    public async Task EveryChunkCarriesTheAudioFormat()
    {
        await using var server = FakeWyomingServer.Start(_ =>
            [WireEvent.Of("transcript", new JsonObject { ["text"] = "x" })]);

        await new WyomingTranscriptionEngine(Options(server)).TranscribeAsync(Audio(Speaking(Ms(300))));

        // A service that missed audio-start would otherwise have to guess the rate.
        foreach (var chunk in server.Received.Where(e => e.Type == "audio-chunk"))
        {
            Assert.Equal(16000, chunk.Data["rate"]!.GetValue<int>());
            Assert.Equal(2, chunk.Data["width"]!.GetValue<int>());
            Assert.Equal(1, chunk.Data["channels"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task TheModelAndLanguageAreSentWhenSetAndOmittedWhenNot()
    {
        await using var named = FakeWyomingServer.Start(_ =>
            [WireEvent.Of("transcript", new JsonObject { ["text"] = "x" })]);
        var options = Options(named) with { Name = "faster-whisper-medium", Language = "en" };
        await new WyomingTranscriptionEngine(options).TranscribeAsync(Audio(Speaking(Ms(200))));

        var request = named.Received.First(e => e.Type == "transcribe");
        Assert.Equal("faster-whisper-medium", request.Data["name"]!.GetValue<string>());
        Assert.Equal("en", request.Data["language"]!.GetValue<string>());

        await using var bare = FakeWyomingServer.Start(_ =>
            [WireEvent.Of("transcript", new JsonObject { ["text"] = "x" })]);
        await new WyomingTranscriptionEngine(Options(bare)).TranscribeAsync(Audio(Speaking(Ms(200))));

        Assert.Empty(bare.Received.First(e => e.Type == "transcribe").Data);
    }

    [Fact]
    public async Task NarrationBeforeTheTranscriptIsIgnored()
    {
        await using var server = FakeWyomingServer.Start(_ =>
        [
            WireEvent.Of("audio-start", Format()),
            WireEvent.Of("audio-stop"),
            WireEvent.Of("transcript", new JsonObject { ["text"] = "the real answer" }),
        ]);

        var result = await new WyomingTranscriptionEngine(Options(server)).TranscribeAsync(Audio(Speaking(Ms(200))));

        Assert.Equal("the real answer", result.Text);
    }

    [Fact]
    public async Task ADataBlockSentSeparatelyIsMergedIntoTheEvent()
    {
        // The protocol allows data inline in the header OR as a length-prefixed block that is
        // merged into it. A reader that handled only the inline form would see an empty transcript.
        await using var server = FakeWyomingServer.Start(_ =>
        [
            WireEvent.Of("transcript", new JsonObject { ["text"] = "sent the long way round" })
                with { SeparateDataBlock = true },
        ]);

        var result = await new WyomingTranscriptionEngine(Options(server)).TranscribeAsync(Audio(Speaking(Ms(200))));

        Assert.Equal("sent the long way round", result.Text);
    }

    [Fact]
    public async Task AServiceThatHangsUpWithoutAnsweringSaysSo()
    {
        await using var server = FakeWyomingServer.Start(_ => []);

        var error = await Assert.ThrowsAsync<IOException>(
            () => new WyomingTranscriptionEngine(Options(server)).TranscribeAsync(Audio(Speaking(Ms(200)))));

        Assert.Contains("without returning a transcript", error.Message);
    }

    [Fact]
    public async Task NothingListeningIsATimeoutRatherThanAHang()
    {
        // Port 1 on loopback: nothing is there, and the connect must give up rather than wait.
        var options = new WyomingOptions
        {
            Host = "127.0.0.1",
            Port = 1,
            Timeout = TimeSpan.FromMilliseconds(600),
        };

        await Assert.ThrowsAnyAsync<Exception>(
            () => new WyomingTranscriptionEngine(options).TranscribeAsync(Audio(Speaking(Ms(200)))));
    }

    [Fact]
    public async Task InitializeGoesThroughTheInterfaceAndReportsReady()
    {
        await using var server = FakeWyomingServer.Start(_ => []);
        var stages = new List<TranscriptionInitializationStage>();

        // Through the interface deliberately: ITranscriptionEngine gives this member a default,
        // so a near-miss signature would compile and strand every interface-typed caller on it.
        ITranscriptionEngine engine = new WyomingTranscriptionEngine(Options(server));
        await engine.InitializeAsync(new SyncProgress<TranscriptionInitializationProgress>(p => stages.Add(p.Stage)));

        Assert.Equal([TranscriptionInitializationStage.Ready], stages);
        Assert.True(engine.IsReady);
    }

    [Fact]
    public async Task DiagnosticsNameTheServiceAndRecordTheLastRun()
    {
        await using var server = FakeWyomingServer.Start(_ =>
            [WireEvent.Of("transcript", new JsonObject { ["text"] = "x" })]);
        var engine = new WyomingTranscriptionEngine(Options(server) with { Name = "faster-whisper-small" });

        Assert.Contains("never", engine.GetDiagnostics().LastRun);

        await engine.TranscribeAsync(Audio(Speaking(Ms(1000))));
        var after = engine.GetDiagnostics();

        Assert.Equal("wyoming", after.Engine);
        Assert.Equal("faster-whisper-small", after.Model);
        Assert.Contains($"{server.Port}", after.ModelPath);
        Assert.Contains("of audio", after.LastRun);
    }
}

/// <summary>The speaking half: Piper and anything else that answers a <c>synthesize</c>.</summary>
public sealed class WyomingSpeechTests
{
    private static WyomingOptions Options(FakeWyomingServer server) =>
        new() { Host = server.Host, Port = server.Port };

    private static JsonObject Format(int rate = 22050, int width = 2, int channels = 1) =>
        new() { ["rate"] = rate, ["width"] = width, ["channels"] = channels };

    private static async Task<List<PcmAudio>> Collect(ITextToSpeech tts, SpeechRequest request)
    {
        var chunks = new List<PcmAudio>();
        await foreach (var chunk in tts.SynthesizeAsync(request))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    [Fact]
    public async Task SpokenAudioArrivesInOrderAndIntact()
    {
        var spoken = Speaking(Ms(600));
        await using var server = FakeWyomingServer.Start(_ =>
        [
            WireEvent.Of("audio-start", Format()),
            WireEvent.Of("audio-chunk", Format(), spoken[..1000]),
            WireEvent.Of("audio-chunk", Format(), spoken[1000..]),
            WireEvent.Of("audio-stop"),
        ]);

        var chunks = await Collect(new WyomingTextToSpeech(Options(server)), new SpeechRequest("three open tasks"));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(spoken, chunks.SelectMany(c => c.Data.ToArray()).ToArray());
    }

    [Fact]
    public async Task TheRateComesFromTheService()
    {
        await using var server = FakeWyomingServer.Start(_ =>
        [
            WireEvent.Of("audio-start", Format(rate: 22050)),
            WireEvent.Of("audio-chunk", Format(rate: 22050), Speaking(Ms(100))),
            WireEvent.Of("audio-stop"),
        ]);

        var chunks = await Collect(new WyomingTextToSpeech(Options(server)), new SpeechRequest("hello"));

        // Piper's voices are 22.05 kHz; assuming 16 kHz would play everything a third too slow.
        Assert.All(chunks, c => Assert.Equal(22050, c.SampleRate));
    }

    [Fact]
    public async Task ARateGivenOnlyAtTheStartStillApplies()
    {
        await using var server = FakeWyomingServer.Start(_ =>
        [
            WireEvent.Of("audio-start", Format(rate: 22050)),
            WireEvent.Of("audio-chunk", payload: Speaking(Ms(100))),      // no format repeated
            WireEvent.Of("audio-stop"),
        ]);

        var chunks = await Collect(new WyomingTextToSpeech(Options(server)), new SpeechRequest("hello"));

        Assert.Equal(22050, Assert.Single(chunks).SampleRate);
    }

    [Fact]
    public async Task AudioStopEndsTheStream()
    {
        await using var server = FakeWyomingServer.Start(_ =>
        [
            WireEvent.Of("audio-start", Format()),
            WireEvent.Of("audio-chunk", Format(), Speaking(Ms(100))),
            WireEvent.Of("audio-stop"),
            WireEvent.Of("audio-chunk", Format(), Speaking(Ms(100))),     // after the end
        ]);

        var chunks = await Collect(new WyomingTextToSpeech(Options(server)), new SpeechRequest("hello"));

        Assert.Single(chunks);
    }

    [Fact]
    public async Task AudioThatIsNotSixteenBitIsRefusedRatherThanPlayedAsNoise()
    {
        await using var server = FakeWyomingServer.Start(_ =>
        [
            WireEvent.Of("audio-start", Format(width: 4)),
            WireEvent.Of("audio-stop"),
        ]);

        var error = await Assert.ThrowsAsync<IOException>(
            () => Collect(new WyomingTextToSpeech(Options(server)), new SpeechRequest("hello")));

        Assert.Contains("32-bit", error.Message);
    }

    [Fact]
    public async Task TheRequestedVoiceWinsOverTheConfiguredOne()
    {
        await using var server = FakeWyomingServer.Start(_ => [WireEvent.Of("audio-stop")]);
        var options = Options(server) with { Name = "en_US-lessac-medium", Language = "en_US" };

        await Collect(new WyomingTextToSpeech(options), new SpeechRequest("hello", Voice: "en_GB-alan-low"));

        var voice = server.Received.Single(e => e.Type == "synthesize").Data["voice"]!.AsObject();
        Assert.Equal("en_GB-alan-low", voice["name"]!.GetValue<string>());
        Assert.Equal("en_US", voice["language"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheTextIsWhatWasAskedFor()
    {
        await using var server = FakeWyomingServer.Start(_ => [WireEvent.Of("audio-stop")]);

        await Collect(new WyomingTextToSpeech(Options(server)), new SpeechRequest("the board has three open tasks"));

        var synthesize = server.Received.Single(e => e.Type == "synthesize");
        Assert.Equal("the board has three open tasks", synthesize.Data["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task EmptyTextIsNotSentAnywhere()
    {
        await using var server = FakeWyomingServer.Start(_ => [WireEvent.Of("audio-stop")]);

        Assert.Empty(await Collect(new WyomingTextToSpeech(Options(server)), new SpeechRequest("   ")));
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task TheVoicesOfferedAreTheOnesConfigured()
    {
        await using var server = FakeWyomingServer.Start(_ => []);
        var options = Options(server) with { Voices = [new VoiceDescriptor("en_US-lessac-medium", "Lessac")] };

        var voices = await new WyomingTextToSpeech(options).GetVoicesAsync();

        Assert.Equal("Lessac", Assert.Single(voices).DisplayName);
    }
}
