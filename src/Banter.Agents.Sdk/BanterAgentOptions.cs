namespace Banter.Agents.Sdk;

public sealed record BanterAgentOptions
{
    public required Uri Server { get; init; }
    public required string User { get; init; }

    /// <summary>
    /// The account password, for an agent that has one. Leave empty when <see cref="PrivateKey"/>
    /// is set — an enrolled agent has no password, and never had one.
    /// </summary>
    public string Password { get; init; } = "";

    /// <summary>
    /// This machine's private key, from enrolment (<c>AgentEnrolment.EnrolAsync</c>). When set, the
    /// agent proves who it is by signing a challenge instead of sending a secret, and the key is
    /// never transmitted.
    ///
    /// <para>Hold it somewhere the operating system protects — DPAPI on Windows, Keychain on
    /// macOS, libsecret on Linux. It is the whole of this agent's identity.</para>
    /// </summary>
    public byte[]? PrivateKey { get; init; }
    public IReadOnlyList<string> Rooms { get; init; } = ["#main"];
    public string ClientName { get; init; } = "Banter.Agent";

    /// <summary>
    /// In <see cref="Protocol.RoomDispatchMode.Mention"/> rooms: answer everything rather than
    /// only messages naming this agent. Suits a dedicated room; will get the agent throttled
    /// anywhere else. Ignored in delegated rooms, where the delegator decides who speaks.
    /// </summary>
    public bool RespondToEveryMessage { get; init; }

    // ── Routing attributes (PLAN §8a), announced on start ────────────────────────────────────

    /// <summary>
    /// Where this agent runs. Defaults to <see cref="Protocol.AgentLocality.Unknown"/>, which the
    /// server treats as frontier and never elects — an agent must state that it is local, because
    /// assuming it is the mistake that leaks data.
    /// </summary>
    public Protocol.AgentLocality Locality { get; init; } = Protocol.AgentLocality.Unknown;

    /// <summary>Most sensitive data this agent may receive. Unknown means no clearance at all.</summary>
    public Protocol.DataSensitivity Clearance { get; init; } = Protocol.DataSensitivity.Unknown;

    /// <summary>Capability tags the delegator matches against (<c>code</c>, <c>github</c>, …).</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>Human-readable summary shown in the roster.</summary>
    public string Description { get; init; } = "";

    /// <summary>Lower is cheaper. A tie-break in election and routing.</summary>
    public int CostTier { get; init; } = 1;

    /// <summary>Ask to be this room's delegator. Only honoured for agents that are eligible.</summary>
    public bool WantsDelegator { get; init; }

    /// <summary>
    /// When set, this agent routes as delegator rather than answering everything itself. Null
    /// keeps the simpler behaviour, which is what a single-agent room wants.
    /// </summary>
    public RoutingOptions? Routing { get; init; }

    /// <summary>
    /// When set, this agent works the ledger (PLAN §8b): it claims open tasks matching its
    /// skills, does them, and reports the result. Null means it ignores tasks entirely.
    /// </summary>
    public TaskWorkOptions? TaskWork { get; init; }
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

    /// <summary>
    /// How many rounds of tool calls one reply may take. A model that keeps calling the same tool
    /// on a result it does not like would otherwise loop until the room, the upstream, or the
    /// token budget gave out — so the loop is bounded and the model is told when it was cut off.
    /// </summary>
    public int MaxToolRounds { get; init; } = 5;
}

/// <summary>
/// Turns an agent into a routing delegator (PLAN §8a). When present on
/// <see cref="BanterAgentOptions.Routing"/>, an elected delegator classifies each human request
/// and hands it to the best-suited agent instead of answering everything itself.
/// </summary>
public sealed record RoutingOptions
{
    /// <summary>
    /// Decides sensitivity and required skills. Defaults to the model-free keyword classifier,
    /// which fails closed — anything it does not recognise as public stays local.
    /// </summary>
    public Banter.Core.IRequestClassifier Classifier { get; init; } = new Banter.Core.KeywordRequestClassifier();

    /// <summary>
    /// Static policy: when false, no request is ever routed to a frontier agent regardless of how
    /// it was classified. This is the setting that beats the model's judgement.
    /// </summary>
    public bool AllowFrontier { get; init; } = true;

    /// <summary>
    /// Say why a request was routed the way it was. Egress announcements are made regardless —
    /// this only controls the ordinary, non-egress explanations.
    /// </summary>
    public bool ExplainDecisions { get; init; } = true;

    /// <summary>
    /// Phrases that ask for more than one agent's answer. Matching one fans the request out to
    /// every eligible agent instead of picking the single best.
    ///
    /// <para>Deliberately an explicit opt-in per request rather than something the delegator
    /// decides on its own: fanning out multiplies cost and room noise, and the same clearance
    /// filter still applies, so it never widens who may see the data.</para>
    /// </summary>
    public IReadOnlyList<string> FanOutPhrases { get; init; } =
        ["everyone", "all of you", "both of you", "each of you", "opinions", "second opinion"];

    /// <summary>
    /// Move a multi-agent request into a sub-room instead of running it in the main one.
    ///
    /// <para>Off by default, deliberately: a fan-out in the main room is noisier but keeps the
    /// humans in it, and a side channel they have to go and find is a side channel they will not
    /// read. Turn it on when the back-and-forth would drown the conversation. The sub-room
    /// inherits the parent's sensitivity either way, so this never changes who may see the
    /// data — only where they say it.</para>
    /// </summary>
    public bool SubRoomForFanOut { get; init; }
}


/// <summary>
/// Makes an agent a worker on the room's task board (PLAN §8b).
/// </summary>
public sealed record TaskWorkOptions
{
    /// <summary>
    /// Claim open tasks whose text matches this agent's skills. When false the agent still
    /// executes tasks <em>assigned</em> to it, but never takes work off the board itself —
    /// which is what you want in a delegated room where routing is somebody else's job.
    /// </summary>
    public bool ClaimOpenTasks { get; init; } = true;

    /// <summary>
    /// How often to renew the lease while working. Must be comfortably under the server's lease,
    /// or a long job is reclaimed mid-flight and handed to somebody else.
    /// </summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMinutes(5);
}
