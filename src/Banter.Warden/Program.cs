using Banter.Agents.Sdk;
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

if (pass is null || model is null || Has("--help") || Has("-h"))
{
    Console.Error.WriteLine("""
        banter-warden - run an LLM agent as a Banter user

          banter-warden --user dagger --pass <secret> --model <id>
                        [--server tcp://127.0.0.1:7770] [--rooms #main,#dev]
                        [--llm http://localhost:1234/v1] [--api-key <key>]
                        [--system "<system prompt>"] [--answer-all]

        By default the agent replies only when its nick is mentioned. --answer-all makes it
        reply to everything, which suits a dedicated room and will get it throttled elsewhere.

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
Console.WriteLine(answerAll ? "Answering every message." : $"Answering when '{user}' is mentioned.");
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
