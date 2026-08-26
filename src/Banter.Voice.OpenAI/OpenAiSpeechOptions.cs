namespace Banter.Voice.OpenAI;

/// <summary>
/// Where speech is sent and under what name (PLAN §6). One set of options covers OpenAI, Qwen
/// through DashScope's OpenAI-compatible surface, and every local server that speaks the same
/// shape (Speaches, LocalAI, vLLM) — the only differences are the base URL, the key, and the
/// model names, which is exactly why the plan calls for one well-built adapter rather than three.
/// </summary>
public sealed record OpenAiSpeechOptions
{
    /// <summary>Base URL up to and including the version segment, e.g. <c>https://api.openai.com/v1</c>.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>Bearer token. Empty for a local server that wants none — the header is then
    /// omitted entirely rather than sent blank, which some servers reject.</summary>
    public string ApiKey { get; init; } = "";

    public string TranscriptionModel { get; init; } = "whisper-1";

    /// <summary>
    /// BCP-47 hint, or null to let the engine detect. Worth setting: detection on a short
    /// utterance is a coin flip, and a misdetected language produces fluent nonsense rather than
    /// an error.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Vocabulary hint passed to the engine. This is where room nicknames and agent names belong:
    /// they are the words an acoustic model has never seen and will confidently replace with
    /// something plausible.
    /// </summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// A ceiling for one request. Generous, because a cold local server loading a model on first
    /// call is slow once and fast afterwards, and failing that first call looks like a broken
    /// configuration.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}
