using Banter.Agents.Sdk;
using Banter.Client.Core;
using Banter.Warden;
using Banter.Protocol;
using Banter.Protocol.Transport;

// Banter.Warden - agent supervisor. Today it runs a single LlmChatAgent from the command line;
// the config-driven fleet described in PLAN is the next step.

// Enrolment: redeem the one-time code an admin handed over, keep the key it produces, and stop.
// Deliberately its own mode rather than something the run path does lazily — enrolling is a thing
// somebody does once, on purpose, while watching.
if (Arg("--enrol") is { Length: > 0 } enrolCode)
{
    var enrolServer = Arg("--server") ?? "tcp://127.0.0.1:7770";
    var keyPath = Arg("--key");
    if (keyPath is null)
    {
        Console.Error.WriteLine("usage: banter-warden --enrol <code> --key <path> [--server tcp://host:port]");
        return 1;
    }

    if (File.Exists(keyPath))
    {
        // Overwriting would strand the identity that key belongs to: the server still has its
        // public half and nothing else can produce the private one.
        Console.Error.WriteLine($"error: {keyPath} already exists. Move it aside first if you mean to replace it.");
        return 1;
    }

    try
    {
        var enrolEndpoint = new Uri(enrolServer);
        var (identity, privateKey) = await AgentEnrolment
            .EnrolAsync(BanterTransports.Client(enrolEndpoint), enrolEndpoint, enrolCode)
            .ConfigureAwait(false);

        await AgentKeyFile.SaveAsync(keyPath, privateKey).ConfigureAwait(false);

        Console.WriteLine($"Enrolled as '{identity.Nick}'.");
        Console.WriteLine($"  key        {Path.GetFullPath(keyPath)}");
        Console.WriteLine($"  identifies {identity.KeyFingerprint}");
        Console.WriteLine($"  rooms      {string.Join(", ", identity.Rooms)}");
        Console.WriteLine();
        Console.WriteLine("The code is spent. This key is what identifies the agent now, it never leaves");
        Console.WriteLine("this machine, and an admin can revoke it at any time. Other agents can be");
        Console.WriteLine("enrolled here too - each keeps its own key file.");
        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException
        or System.Net.Sockets.SocketException or ArgumentException or UriFormatException)
    {
        Console.Error.WriteLine($"error: could not enrol: {ex.Message}");
        return 1;
    }
}

// Fleet mode: one config file, many agents, supervised. Falls through to the single-agent
// command line below when no --fleet is given.
if (Arg("--fleet") is { Length: > 0 } fleetPath)
{
    return await RunFleetAsync(fleetPath);
}

var server = Arg("--server") ?? "tcp://127.0.0.1:7770";
var user = Arg("--user") ?? "dagger";
var pass = Arg("--pass") ?? Environment.GetEnvironmentVariable("BANTER_PASS");
var keyFile = Arg("--key") ?? Environment.GetEnvironmentVariable("BANTER_KEY");
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
var workMode = (Arg("--work-mode") ?? "delegate-and-work").ToLowerInvariant() switch
{
    "delegate-only" => AgentWorkMode.DelegateOnly,
    "work-when-alone" => AgentWorkMode.WorkWhenAlone,
    _ => AgentWorkMode.DelegateAndWork,
};
var noFrontier = Has("--no-frontier");
var smartClassifier = Has("--llm-classify");
var worksTasks = Has("--work-tasks");
var assignedOnly = Has("--assigned-only");

if ((pass is null && keyFile is null) || model is null || Has("--help") || Has("-h"))
{
    Console.Error.WriteLine("""
        banter-warden - run an LLM agent as a Banter user

          banter-warden --enrol <code> --key <path>   redeem an enrolment code, once
          banter-warden --fleet <fleet.json>          run a supervised fleet
          banter-warden --user dagger --key <path> --model <id>      run one enrolled agent
          banter-warden --user dagger --pass <secret> --model <id>   run one agent
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
          --work-mode <mode>    what to do, while delegator, with work nobody else can take:
                                delegate-and-work (default), delegate-only, or work-when-alone.
                                Answering holds this agent's turn until it finishes, and a
                                delegator mid-answer cannot hand anything out.
          --no-frontier         never route anything to a frontier agent, whatever the
                                classification says. Static policy beats the model.
          --llm-classify        use the model to classify request sensitivity instead of the
                                keyword rules. Explicit sensitive terms still veto it, and any
                                classifier failure falls back to sensitive.
          --work-tasks          work the room's task board: claim open tasks matching this
                                agent's skills, do them, and report the result
          --assigned-only       with --work-tasks, only run tasks assigned to this agent -
                                never take work off the open board

        --pass also reads BANTER_PASS; --model reads BANTER_MODEL; --llm reads BANTER_LLM.
        """);
    return 1;
}

if (keyFile is { Length: > 0 } && !AgentKeyFile.IsUsable(keyFile))
{
    // Report the key file as itself rather than letting the server say "invalid credentials",
    // which would send someone to the wrong machine. The missing-file case is the first F5 on a
    // fresh checkout, so the message says exactly what to do about it.
    Console.Error.WriteLine(File.Exists(keyFile)
        ? $"error: {keyFile} exists but is not a usable private key. If it was truncated or replaced, an admin must reissue the identity and you must enrol again."
        : $"error: no key at {keyFile}. Create the agent on the admin UI's agents page (or /agent add in Banter.Cli), then redeem the one-time code it shows:\n\n  banter-warden --enrol <code> --key {keyFile} --server {server}");
    return 1;
}

var agentOptions = new BanterAgentOptions
{
    Server = new Uri(server),
    User = user,

    // A key when one was given, a password otherwise. An enrolled agent has no password and
    // never had one — what identifies it is a file on this machine that has never been sent.
    Password = pass ?? "",
    PrivateKey = keyFile is { Length: > 0 } ? File.ReadAllBytes(keyFile) : null,
    Rooms = rooms,
    ClientName = "Banter.Warden",
    RespondToEveryMessage = answerAll,
    Locality = locality,
    Clearance = clearance,
    Skills = skills,
    Description = Arg("--description") ?? $"{model} via {endpoint}",
    CostTier = costTier,
    WantsDelegator = Has("--delegator"),
    WorkMode = workMode,
    TaskWork = worksTasks ? new TaskWorkOptions { ClaimOpenTasks = !assignedOnly } : null,
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

// What the server granted, against what the flags above asked for. The command line is a request:
// for a managed agent the identity record answers, and pretending otherwise here would leave an
// operator debugging routing against attributes this process does not actually have. Fires on
// start and again if an admin changes the identity while this runs.
agent.AttributesSet += granted =>
{
    var asRequested = granted.Locality == locality
        && granted.Clearance == clearance
        && granted.Skills.SequenceEqual(skills)
        && granted.CostTier == costTier
        && granted.WantsDelegator == Has("--delegator");
    if (!asRequested)
    {
        Console.WriteLine(
            $"Admin overrides in effect - granted: {granted.Locality}, clearance {granted.Clearance}, " +
            $"skills [{string.Join(", ", granted.Skills)}], cost {granted.CostTier}, " +
            $"delegator {(granted.WantsDelegator ? "pinned" : "not pinned")}.");
    }
};

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
Console.WriteLine($"Requested {locality}, clearance {clearance}, skills [{string.Join(", ", skills)}] - the server's answer follows if it differs.");
if (worksTasks)
{
    Console.WriteLine(assignedOnly
        ? "Working assigned tasks only."
        : "Working the task board; claiming open tasks that match its skills.");
}

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
if (agent.EvictedReason is { } evictedBecause)
{
    // Not an outage and not a crash: the server ended this identity on purpose. Exit non-zero so
    // a supervisor restarts nothing - a restart would just be refused at the door.
    Console.Error.WriteLine($"evicted: {evictedBecause}");
    return 1;
}

return 0;

async Task<int> RunFleetAsync(string path)
{
    FleetConfig fleet;
    try
    {
        fleet = FleetConfig.Load(path);
    }
    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
    {
        Console.Error.WriteLine($"error: could not read {path}: {ex.Message}");
        return 1;
    }

    // Report every problem, not just the first: fixing a config one error per run is miserable.
    var problems = fleet.Validate();
    if (problems.Count > 0)
    {
        Console.Error.WriteLine($"error: {path} has {problems.Count} problem(s):");
        foreach (var problem in problems)
        {
            Console.Error.WriteLine($"  - {problem}");
        }

        return 1;
    }

    var supervisor = new FleetSupervisor(fleet, agentConfig =>
    {
        var (options, llm) = FleetSupervisor.BuildOptions(fleet, agentConfig);
        return new LlmChatAgent(options, llm);
    });

    supervisor.Reported += message => Console.WriteLine($"[warden] {message}");

    Console.WriteLine($"Fleet: {fleet.Agents.Count} agent(s) against {fleet.Server}");
    Console.WriteLine("Press Ctrl+C to stop.");

    using var fleetStopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        fleetStopping.Cancel();
    };

    try
    {
        await supervisor.RunAsync(() => new TcpBanterTransport(), fleetStopping.Token);
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C.
    }

    return 0;
}

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

bool Has(string name) => Array.IndexOf(args, name) >= 0;
