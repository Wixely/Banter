using System.Net;
using Bantz.Speech;
using Banter.Voice.OpenAI;
using Xunit;
using static Banter.Voice.Tests.Pcm;

namespace Banter.Voice.Tests;

/// <summary>
/// Synthesis arrives in pieces over a socket, so most of what can go wrong here is reassembly:
/// a sample split across two chunks is a click, and a sample rate taken from the wrong place is
/// every agent in the room speaking at the wrong pitch.
/// </summary>
public sealed class OpenAiTextToSpeechTests
{
    private static OpenAiSpeechOptions Options() => new() { Endpoint = new Uri("http://localhost:8000/v1") };

    private static byte[] Wave(byte[] samples, int sampleRate, int channels = 1)
    {
        using var stream = new PcmAudio(samples, sampleRate, channels).CreateWaveStream();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static async Task<List<PcmAudio>> Collect(ITextToSpeech tts, SpeechRequest request)
    {
        var chunks = new List<PcmAudio>();
        await foreach (var chunk in tts.SynthesizeAsync(request))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static byte[] Joined(List<PcmAudio> chunks) => chunks.SelectMany(c => c.Data.ToArray()).ToArray();

    [Fact]
    public async Task RawSamplesComeBackWholeAndInOrder()
    {
        var spoken = Speaking(Ms(500));
        using var tts = new OpenAiTextToSpeech(Options(), StubHandler.Audio(spoken));

        var chunks = await Collect(tts, new SpeechRequest("the board has three open tasks"));

        Assert.True(chunks.Count > 1, "expected the reply to be delivered in pieces");
        Assert.Equal(spoken, Joined(chunks));
    }

    [Fact]
    public async Task NoSampleIsSplitAcrossTwoChunks()
    {
        // 101 bytes per read lands mid-sample every time; a chunk with an odd length would hand a
        // consumer half of one sample and half of the next.
        using var tts = new OpenAiTextToSpeech(Options(), StubHandler.Audio(Speaking(Ms(300)), bytesPerRead: 101));

        var chunks = await Collect(tts, new SpeechRequest("hello"));

        Assert.All(chunks, c => Assert.Equal(0, c.Data.Length % 2));
    }

    [Fact]
    public async Task RawSamplesAreLabelledWithTheConfiguredRate()
    {
        // Nothing in a headerless reply states the rate, so the configuration is all there is.
        var options = Options() with { PcmSampleRate = 22050 };
        using var tts = new OpenAiTextToSpeech(options, StubHandler.Audio(Speaking(Ms(200))));

        var chunks = await Collect(tts, new SpeechRequest("hello"));

        Assert.All(chunks, c => Assert.Equal(22050, c.SampleRate));
    }

    [Fact]
    public async Task AWavHeaderOverridesTheConfiguredRate()
    {
        var samples = Speaking(Ms(400));
        var options = Options() with { Format = SpeechAudioFormat.Wav, PcmSampleRate = 24000 };
        using var tts = new OpenAiTextToSpeech(options, StubHandler.Audio(Wave(samples, 16000)));

        var chunks = await Collect(tts, new SpeechRequest("hello"));

        // The server said 16 kHz; believing the configured 24 kHz would play it half again too fast.
        Assert.All(chunks, c => Assert.Equal(16000, c.SampleRate));
        Assert.Equal(samples, Joined(chunks));
    }

    [Fact]
    public async Task TheHeaderIsStrippedEvenWhenItArrivesSplitAcrossReads()
    {
        var samples = Speaking(Ms(200));
        var options = Options() with { Format = SpeechAudioFormat.Wav };
        using var tts = new OpenAiTextToSpeech(options, StubHandler.Audio(Wave(samples, 24000), bytesPerRead: 7));

        var chunks = await Collect(tts, new SpeechRequest("hello"));

        Assert.Equal(samples, Joined(chunks));
    }

    [Fact]
    public async Task AWavThatIsNotSixteenBitIsRefusedRatherThanPlayedAsNoise()
    {
        var options = Options() with { Format = SpeechAudioFormat.Wav };
        var eightBit = Wave(Speaking(Ms(100)), 24000);
        eightBit[34] = 8;                                   // bits-per-sample field

        using var tts = new OpenAiTextToSpeech(options, StubHandler.Audio(eightBit));

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => Collect(tts, new SpeechRequest("hello")));
        Assert.Contains("8-bit", error.Message);
    }

    [Fact]
    public async Task AReplyThatIsNotAWavAtAllIsRefused()
    {
        var options = Options() with { Format = SpeechAudioFormat.Wav };
        using var tts = new OpenAiTextToSpeech(
            options,
            StubHandler.Audio(System.Text.Encoding.ASCII.GetBytes("<html>502 Bad Gateway</html>")));

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => Collect(tts, new SpeechRequest("hello")));
        Assert.Contains("readable WAV", error.Message);
    }

    [Fact]
    public async Task ItPostsTheModelTextVoiceAndFormat()
    {
        var options = Options() with { SpeechModel = "kokoro", DefaultVoice = "af_heart" };
        var handler = StubHandler.Audio(Speaking(Ms(50)));
        using var tts = new OpenAiTextToSpeech(options, handler);

        await Collect(tts, new SpeechRequest("three open tasks", Voice: "onyx", Speed: 1.25));

        Assert.Equal("http://localhost:8000/v1/audio/speech", handler.Request!.RequestUri!.ToString());
        Assert.Contains("\"model\":\"kokoro\"", handler.Body);
        Assert.Contains("\"input\":\"three open tasks\"", handler.Body);
        Assert.Contains("\"voice\":\"onyx\"", handler.Body);
        Assert.Contains("\"response_format\":\"pcm\"", handler.Body);
        Assert.Contains("\"speed\":1.25", handler.Body);
    }

    [Fact]
    public async Task ARequestWithNoVoiceGetsTheConfiguredDefault()
    {
        var options = Options() with { DefaultVoice = "af_heart" };
        var handler = StubHandler.Audio(Speaking(Ms(50)));
        using var tts = new OpenAiTextToSpeech(options, handler);

        await Collect(tts, new SpeechRequest("hello"));

        Assert.Contains("\"voice\":\"af_heart\"", handler.Body);
    }

    [Fact]
    public async Task WavIsAskedForWhenWavIsConfigured()
    {
        var handler = StubHandler.Audio(Wave(Speaking(Ms(50)), 24000));
        using var tts = new OpenAiTextToSpeech(Options() with { Format = SpeechAudioFormat.Wav }, handler);

        await Collect(tts, new SpeechRequest("hello"));

        Assert.Contains("\"response_format\":\"wav\"", handler.Body);
    }

    [Fact]
    public async Task EmptyTextIsNotSentAnywhere()
    {
        var handler = StubHandler.Audio(Speaking(Ms(50)));
        using var tts = new OpenAiTextToSpeech(Options(), handler);

        // Speaking a stream sentence-by-sentence produces empty fragments; each one would
        // otherwise be a round trip that synthesises silence.
        Assert.Empty(await Collect(tts, new SpeechRequest("   ")));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AFailedRequestCarriesTheStatusAndTheServersComplaint()
    {
        using var tts = new OpenAiTextToSpeech(
            Options(),
            StubHandler.Json("""{"error":{"message":"unknown voice"}}""", HttpStatusCode.BadRequest));

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => Collect(tts, new SpeechRequest("hello", Voice: "nope")));

        Assert.Contains("400", error.Message);
        Assert.Contains("unknown voice", error.Message);
    }

    [Fact]
    public async Task TheVoicesOfferedAreTheOnesConfigured()
    {
        var options = Options() with { Voices = [new VoiceDescriptor("af_heart", "Heart", "en-US")] };
        using var tts = new OpenAiTextToSpeech(options, StubHandler.Audio([]));

        var voices = await tts.GetVoicesAsync();

        // Configuration, not discovery: the speech API has no endpoint that lists voices.
        var only = Assert.Single(voices);
        Assert.Equal("af_heart", only.Id);
        Assert.Equal("Heart", only.DisplayName);
    }
}
