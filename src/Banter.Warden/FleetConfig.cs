using System.Text.Json;
using System.Text.Json.Serialization;
using Banter.Agents.Sdk;
using Banter.Protocol;

namespace Banter.Warden;

/// <summary>
/// A fleet of agents, read from JSON. One realistic room needs a local delegator, a specialist and
/// often a frontier researcher; running that from command-line flags means three terminals and no
/// restart handling, which is what this replaces.
///
/// <para><b>No secrets in this file.</b> Passwords and API keys come from environment variables
/// named per agent, because a fleet config is exactly the kind of thing that gets committed.</para>
/// </summary>
public sealed record FleetConfig
{
    public string Server { get; init; } = "tcp://127.0.0.1:7770";

    /// <summary>Default LLM endpoint for agents that do not name their own.</summary>
    public string Llm { get; init; } = "http://localhost:1234/v1";

    public List<AgentConfig> Agents { get; init; } = [];

    /// <summary>How supervision reacts to an agent that stops.</summary>
    public RestartConfig Restart { get; init; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static FleetConfig Load(string path) =>
        JsonSerializer.Deserialize<FleetConfig>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} did not contain a fleet configuration.");

    /// <summary>Parse without touching disk — used by the tests and by config validation.</summary>
    public static FleetConfig Parse(string json) =>
        JsonSerializer.Deserialize<FleetConfig>(json, Json)
        ?? throw new InvalidDataException("Empty fleet configuration.");

    /// <summary>
    /// Problems that would make this fleet misbehave rather than fail outright — a duplicate nick,
    /// a frontier agent cleared for sensitive data, a delegator that could never be elected.
    /// Reported at startup so they are noticed before they cause confusing behaviour in a room.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Agents.Count == 0)
        {
            problems.Add("no agents configured");
        }

        foreach (var duplicate in Agents.GroupBy(a => a.User, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            // Two processes on one account share a nick: presence is per account, so they would
            // be one participant with two brains answering.
            problems.Add($"'{duplicate.Key}' is configured more than once - each agent needs its own account");
        }

        foreach (var agent in Agents)
        {
            if (agent.Locality == AgentLocality.Frontier && agent.Clearance == DataSensitivity.Sensitive)
            {
                problems.Add(
                    $"'{agent.User}' is frontier but cleared for sensitive data - that combination " +
                    "sends private content to a third party");
            }

            if (agent.Delegator && agent.Locality != AgentLocality.Local)
            {
                problems.Add(
                    $"'{agent.User}' asks to be delegator but is not local - a delegator reads every " +
                    "message in the room, so it will not be elected in one carrying sensitive content");
            }

            if (agent.Model.Length == 0)
            {
                problems.Add($"'{agent.User}' has no model");
            }

            // Named rather than silently preferred: an operator who set both has one of them in
            // mind, and quietly using the other is how a rotated password appears not to work.
            if (agent.KeyFile is { Length: > 0 } && agent.PasswordEnv is { Length: > 0 })
            {
                problems.Add(
                    $"'{agent.User}' has both a keyFile and a passwordEnv - it needs one or the other");
            }
        }

        return problems;
    }
}

public sealed record RestartConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>First retry delay; doubles up to <see cref="MaxDelaySeconds"/>.</summary>
    public int InitialDelaySeconds { get; init; } = 2;

    public int MaxDelaySeconds { get; init; } = 60;

    /// <summary>
    /// Give up after this many consecutive failures. An agent whose endpoint is gone would
    /// otherwise reconnect forever, and a fleet that never reports a dead member is worse than
    /// one that stops trying and says so.
    /// </summary>
    public int MaxAttempts { get; init; } = 10;
}

public sealed record AgentConfig
{
    public required string User { get; init; }

    /// <summary>
    /// Environment variable holding this agent's password. Defaults to
    /// <c>BANTER_AGENT_&lt;USER&gt;_PASSWORD</c> with the nick upper-cased and non-alphanumerics
    /// replaced by underscores. Ignored when <see cref="KeyFile"/> is set.
    /// </summary>
    public string? PasswordEnv { get; init; }

    /// <summary>
    /// Where this machine's private key lives, for an agent enrolled with a code rather than given
    /// a password. Written by <c>banter-warden --enrol</c> and never transmitted.
    ///
    /// <para>Preferred over a password: it cannot be replayed from a captured login, it is useless
    /// on any other machine once an admin reissues, and revoking it is a row delete on the
    /// server.</para>
    /// </summary>
    public string? KeyFile { get; init; }

    public List<string> Rooms { get; init; } = ["#main"];
    public string Model { get; init; } = "";

    /// <summary>Overrides the fleet's endpoint, for a mixed local/hosted fleet.</summary>
    public string? Llm { get; init; }

    public string? ApiKeyEnv { get; init; }
    public string? System { get; init; }

    public AgentLocality Locality { get; init; } = AgentLocality.Local;
    public DataSensitivity Clearance { get; init; } = DataSensitivity.Sensitive;
    public List<string> Skills { get; init; } = ["chat"];
    public int Cost { get; init; } = 1;

    public bool Delegator { get; init; }

    /// <summary>Route as delegator rather than answering everything itself (PLAN §8a).</summary>
    public bool Route { get; init; }

    /// <summary>Never route to a frontier agent, whatever the classifier concludes.</summary>
    public bool NoFrontier { get; init; }

    /// <summary>Classify with the model instead of the keyword rules.</summary>
    public bool LlmClassify { get; init; }

    /// <summary>Work the room's task board (PLAN §8b).</summary>
    public bool WorkTasks { get; init; }

    /// <summary>With <see cref="WorkTasks"/>, only run tasks assigned to this agent.</summary>
    public bool AssignedOnly { get; init; }

    /// <summary>Answer every message in a mention-mode room.</summary>
    public bool AnswerAll { get; init; }

    /// <summary>Conventional environment variable name for this agent's password.</summary>
    public string ResolvedPasswordEnv => PasswordEnv ?? $"BANTER_AGENT_{Sanitise(User)}_PASSWORD";

    public string? ResolvedApiKeyEnv => ApiKeyEnv;

    private static string Sanitise(string user) =>
        new(user.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());
}
