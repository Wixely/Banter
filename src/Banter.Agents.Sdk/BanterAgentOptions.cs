namespace Banter.Agents.Sdk;

public sealed record BanterAgentOptions
{
    public required Uri Server { get; init; }
    public required string User { get; init; }
    public required string Password { get; init; }
    public IReadOnlyList<string> Rooms { get; init; } = ["#main"];
    public string ClientName { get; init; } = "Banter.Agent";

    /// <summary>
    /// When false (the default) the agent only answers messages that mention its nick. Turning it
    /// on makes the agent answer everything, which is what you want for a dedicated room and what
    /// will get it throttled anywhere else.
    /// </summary>
    public bool RespondToEveryMessage { get; init; }
}

public sealed record LlmChatAgentOptions
{
    /// <summary>OpenAI-compatible base URL, e.g. LM Studio's <c>http://localhost:1234/v1</c>.</summary>
    public required Uri Endpoint { get; init; }

    public required string Model { get; init; }

    /// <summary>Bearer token. Empty for a local endpoint that does not check one.</summary>
    public string ApiKey { get; init; } = "";

    public string SystemPrompt { get; init; } =
        "You are a participant in a group chat. Keep replies short and conversational - " +
        "a sentence or two unless asked for detail. Do not prefix your replies with your name.";

    /// <summary>
    /// Messages of prior room context to send with each turn. Small on purpose: a chat room is
    /// mostly recent context, and a local model's window fills fast — the Phase 0 spike measured
    /// 3,316 tokens of system prompt and tool schemas before any conversation at all.
    /// </summary>
    public int ContextMessages { get; init; } = 20;

    public double Temperature { get; init; } = 0.7;

    /// <summary>Cap on reply length, so a runaway model cannot flood a room.</summary>
    public int MaxOutputTokens { get; init; } = 512;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}
