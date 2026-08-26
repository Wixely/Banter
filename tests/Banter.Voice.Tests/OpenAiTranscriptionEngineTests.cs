using System.Net;
using Banter.Voice.OpenAI;
using Xunit;
using static Banter.Voice.Tests.Pcm;

namespace Banter.Voice.Tests;

/// <summary>
/// One adapter is meant to cover OpenAI, Qwen and every local server of the same shape (§6), so
/// these pin down what it puts on the wire and how forgiving it is about what comes back —
/// servers in this family agree on very little beyond the <c>text</c> field.
/// </summary>
public sealed class OpenAiTranscriptionEngineTests
{
    private static OpenAiSpeechOptions Options(string endpoint = "http://localhost:8000/v1") =>
        new() { Endpoint = new Uri(endpoint) };

    /// <summary>Form parts, quote-insensitive: HttpClient writes <c>name=file</c> unquoted, and
    /// the test is about which fields were sent, not about how the framework spells them.</summary>
    private static string Fields(StubHandler handler) => handler.Body.Replace("\"", "");

    private static async Task<(StubHandler Handler, TranscriptionResultOrThrow Result)> Transcribe(
        OpenAiSpeechOptions options,
        StubHandler handler)
    {
        using var engine = new OpenAiTranscriptionEngine(options, handler);
        try
        {
            var result = await engine.TranscribeAsync(Audio(Speaking(Ms(300))));
            return (handler, new TranscriptionResultOrThrow(result, null));
        }
        catch (HttpRequestException e)
        {
            return (handler, new TranscriptionResultOrThrow(null, e));
        }
    }

    internal sealed record TranscriptionResultOrThrow(Bantz.Speech.TranscriptionResult? Value, HttpRequestException? Error);

    [Fact]
    public async Task TheTranscriptComesBack()
    {
        var (_, result) = await Transcribe(Options(), StubHandler.Json("""{"text":"open the task board"}"""));

        Assert.Null(result.Error);
        Assert.Equal("open the task board", result.Value!.Text);
    }

    [Fact]
    public async Task SurroundingWhitespaceIsRemoved()
    {
        // Whisper-family servers routinely pad the transcript with a leading space.
        var (_, result) = await Transcribe(Options(), StubHandler.Json("""{"text":"  hello there \n"}"""));

        Assert.Equal("hello there", result.Value!.Text);
    }

    [Fact]
    public async Task ItPostsAudioAndTheModelName()
    {
        var options = Options() with { TranscriptionModel = "Systran/faster-whisper-large-v3" };

        var (handler, _) = await Transcribe(options, StubHandler.Json("""{"text":"x"}"""));

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Contains("name=file", Fields(handler));
        Assert.Contains("filename=audio.wav", Fields(handler));
        Assert.Contains("RIFF", handler.Body);
        Assert.Contains("Systran/faster-whisper-large-v3", handler.Body);
    }

    [Fact]
    public async Task TheUrlIsTheEndpointPlusTheRouteWithNoDoubledSlash()
    {
        var (handler, _) = await Transcribe(Options("http://localhost:8000/v1/"), StubHandler.Json("""{"text":"x"}"""));

        Assert.Equal("http://localhost:8000/v1/audio/transcriptions", handler.Request!.RequestUri!.ToString());
    }

    [Fact]
    public async Task LanguageAndVocabularyHintsAreSentWhenSetAndOmittedWhenNot()
    {
        var hinted = Options() with { Language = "en", Prompt = "Warden, DaggerAgent, CupriNet" };

        var (withHints, _) = await Transcribe(hinted, StubHandler.Json("""{"text":"x"}"""));
        var (without, _) = await Transcribe(Options(), StubHandler.Json("""{"text":"x"}"""));

        Assert.Contains("CupriNet", withHints.Body);
        Assert.Contains("name=language", Fields(withHints));
        Assert.DoesNotContain("name=language", Fields(without));
        Assert.DoesNotContain("name=prompt", Fields(without));
    }

    [Fact]
    public async Task AKeyIsSentAsABearerTokenAndAnEmptyOneIsNotSentAtAll()
    {
        var keyed = Options() with { ApiKey = "sk-test" };

        var (withKey, _) = await Transcribe(keyed, StubHandler.Json("""{"text":"x"}"""));
        var (without, _) = await Transcribe(Options(), StubHandler.Json("""{"text":"x"}"""));

        Assert.Equal("Bearer", withKey.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", withKey.Request.Headers.Authorization.Parameter);

        // A blank Authorization header is worse than none: local servers that want no auth reject it.
        Assert.Null(without.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task TheServersLanguageIsPreferredOverTheOneWeAskedFor()
    {
        var options = Options() with { Language = "en" };

        var (_, result) = await Transcribe(options, StubHandler.Json("""{"text":"bonjour","language":"fr"}"""));

        Assert.Equal("fr", result.Value!.Language);
    }

    [Fact]
    public async Task WithoutOneReportedTheRequestedLanguageStands()
    {
        var options = Options() with { Language = "en" };

        var (_, result) = await Transcribe(options, StubHandler.Json("""{"text":"hello"}"""));

        Assert.Equal("en", result.Value!.Language);
    }

    [Fact]
    public async Task AFailedRequestCarriesTheStatusAndTheServersComplaint()
    {
        var handler = StubHandler.Json("""{"error":{"message":"model not found"}}""", HttpStatusCode.NotFound);

        var (_, result) = await Transcribe(Options(), handler);

        Assert.NotNull(result.Error);
        Assert.Contains("404", result.Error!.Message);
        Assert.Contains("model not found", result.Error.Message);
    }

    [Fact]
    public async Task AReplyThatIsNotJsonFailsWithTheBodyInTheMessage()
    {
        // What a reverse proxy in front of a stopped server returns.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>502 Bad Gateway</html>"),
        });

        var (_, result) = await Transcribe(Options(), handler);

        Assert.NotNull(result.Error);
        Assert.Contains("502 Bad Gateway", result.Error!.Message);
    }

    [Fact]
    public async Task AReplyWithNoTextFieldFails()
    {
        var (_, result) = await Transcribe(Options(), StubHandler.Json("""{"segments":[]}"""));

        Assert.NotNull(result.Error);
        Assert.Contains("no text", result.Error!.Message);
    }

    [Fact]
    public async Task InitializeReportsReadyWithoutTouchingTheNetwork()
    {
        var handler = StubHandler.Json("""{"text":"x"}""");
        using var concrete = new OpenAiTranscriptionEngine(Options(), handler);
        var stages = new List<Bantz.Speech.TranscriptionInitializationStage>();

        // Through the interface deliberately. ITranscriptionEngine gives this member a default
        // implementation, so a signature that is merely close compiles fine and leaves every
        // interface-typed caller — which is all of them — talking to the default instead.
        Bantz.Speech.ITranscriptionEngine engine = concrete;
        await engine.InitializeAsync(new Progress<Bantz.Speech.TranscriptionInitializationProgress>(
            p => stages.Add(p.Stage)));

        // Nothing to download, so the only honest progress report is that it is already done.
        Assert.Equal(0, handler.Calls);
        Assert.Equal([Bantz.Speech.TranscriptionInitializationStage.Ready], stages);
        Assert.True(engine.IsReady);
    }

    [Fact]
    public async Task DiagnosticsNameTheModelAndTheEndpointAndRecordTheLastRun()
    {
        var options = Options() with { TranscriptionModel = "whisper-1" };
        using var engine = new OpenAiTranscriptionEngine(options, StubHandler.Json("""{"text":"x"}"""));

        Assert.Contains("never", engine.GetDiagnostics().LastRun);

        await engine.TranscribeAsync(Audio(Speaking(Ms(1000))));
        var after = engine.GetDiagnostics();

        Assert.Equal("whisper-1", after.Model);
        Assert.Equal("remote", after.Runtime);
        Assert.Contains("localhost:8000", after.ModelPath);
        Assert.Contains("of audio", after.LastRun);
    }
}
