using Banter.Agents.Sdk;
using Banter.Protocol;
using Banter.Protocol.Transport;

// Banter.Warden - agent supervisor. Today it runs a single LlmChatAgent from the command line;
// the config-driven fleet described in PLAN is the next step.

var server = Arg("--server") ?? "tcp://127.0.0.1:7770";
var user = Arg("--user") ?? "dagger";
var pass = Arg("--pass") ?? Environment.GetEnvironmentVariable("BANTER_PASS");
var rooms = (Arg("--rooms") ?? Arg("--room") ?? "#main")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var endpoint = Arg("--llm") ?? Environment.GetEnvironmentVariable("BANTER_LLM") ?? "http://localhost:1234/v1";
var model = Arg("--model") ?? Environment.GetEnvironmentVariable("BANTER_MODEL");
var apiKey = Arg("--api-key") ?? Environment.GetEnvironmentVariable("BANTER_LLM_KEY") ?? "";
var systemPrompt = Arg("--system");
var answerAll = Has("--answer-all");
var locality = Has("--frontier") ? AgentLocality.Frontier : AgentLocality.Local;
var clearance = (Arg("--clearance") ?? "sensitive").ToLowerInvariant() switch
{
    "public" => DataSensitivity.Public,
    "internal" => DataSensitivity.Internal,
    _ => DataSensitivity.Sensitive,
};
var skills = (Arg("--skills") ?? "chat").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var costTier = int.TryParse(Arg("--cost"), out var parsedCost) ? parsedCost : 1;
var routes = Has("--route");
var noFrontier = Has("--no-frontier");
var smartClassifier = Has("--llm-classify");

if (pass is null || model is null || Has("--help") || Has("-h"))
{
    Console.Error.WriteLine("""
        banter-warden - run an LLM agent as a Banter user

          banter-warden --user dagger --pass <secret> --model <id>
                        [--server tcp://127.0.0.1:7770] [--rooms #main,#dev]
                        [--llm http://localhost:1234/v1] [--api-key <key>]
                        [--system "<system prompt>"] [--answer-all]

        Rooms are delegated by default: one elected agent acts on human messages and hands
        work to the others by name. --answer-all only applies in mention-mode rooms.

        Routing attributes (PLAN 8a):
          --frontier            this agent is a third-party model; data sent to it leaves.
                                Omit for a local agent, which is what gets elected delegator.
          --clearance <level>   public | internal | sensitive (default sensitive)
          --skills a,b,c        capability tags the delegator matches on (default chat)
          --cost <n>            lower is cheaper; a tie-break only
          --delegator           ask to be this room's delegator
          --route               when elected delegator, classify each request and hand it to
                                the best-suited agent instead of answering everything
          --no-frontier         never route anything to a frontier agent, whatever the
                                classification says. Static policy beats the model.
          --llm-classify        use the model to classify request sensitivity instead of the
                                keyword rules. Explicit sensitive terms still veto it, and any
                                classifier failure falls back to sensitive.

        --pass also reads BANTER_PASS; --model reads BANTER_MODEL; --llm reads BANTER_LLM.
        """);
    return 1;
}

var agentOptions = new BanterAgentOptions
{
    Server = new Uri(server),
    User = user,
    Password = pass,
    Rooms = rooms,
    ClientName = "Banter.Warden",
    RespondToEveryMessage = answerAll,
    Locality = locality,
    Clearance = clearance,
    Skills = skills,
    Description = Arg("--description") ?? $"{model} via {endpoint}",
    CostTier = costTier,
    WantsDelegator = Has("--delegator"),
    Routing = routes
        ? new RoutingOptions
        {
            AllowFrontier = !noFrontier,
            Classifier = smartClassifier
                ? new LlmRequestClassifier(new OpenAiChatClient(new LlmChatAgentOptions
                {
                    Endpoint = new Uri(endpoint),
                    Model = model,
                    ApiKey = apiKey,
                    // Classification is a gate on every message: a slow one stalls the room.
                    Timeout = TimeSpan.FromSeconds(45),
                    MaxOutputTokens = 200,
                    Temperature = 0,
                }))
                : new Banter.Core.KeywordRequestClassifier(),
        }
        : null,
};

var llmOptions = new LlmChatAgentOptions
{
    Endpoint = new Uri(endpoint),
    Model = model,
    ApiKey = apiKey,
};

if (systemPrompt is { Length: > 0 })
{
    llmOptions = llmOptions with { SystemPrompt = systemPrompt };
}

await using var agent = new LlmChatAgent(agentOptions, llmOptions);
agent.TurnStarted += (room, sender) => Console.WriteLine($"[{room}] answering {sender}...");

try
{
    await agent.StartAsync(new TcpBanterTransport());
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: could not start agent: {ex.Message}");
    return 1;
}

Console.WriteLine($"{user} is in {string.Join(", ", rooms)} using {model} at {endpoint}");
Console.WriteLine($"Announced as {locality}, clearance {clearance}, skills [{string.Join(", ", skills)}].");
if (routes)
{
    Console.WriteLine(noFrontier
        ? "Routing as delegator; frontier agents are blocked by policy."
        : "Routing as delegator; frontier hand-offs will be announced in the room.");
}
Console.WriteLine("Press Ctrl+C to stop.");

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

await agent.RunAsync(stopping.Token);
return 0;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

bool Has(string name) => Array.IndexOf(args, name) >= 0;
