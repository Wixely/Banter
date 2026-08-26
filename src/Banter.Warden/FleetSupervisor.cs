using Banter.Agents.Sdk;
using Banter.Protocol.Transport;

namespace Banter.Warden;

/// <summary>
/// Runs a fleet of agents and keeps them running. Each agent gets its own supervision loop, so one
/// crashing or losing its endpoint does not disturb the others.
/// </summary>
public sealed class FleetSupervisor(FleetConfig config, Func<AgentConfig, BanterAgent> factory)
{
    /// <summary>Raised for anything an operator would want to see: starts, failures, giving up.</summary>
    public event Action<string>? Reported;

    public async Task RunAsync(Func<IBanterClientTransport> transport, CancellationToken cancellationToken)
    {
        var agents = config.Agents.Select(a => SuperviseAsync(a, transport, cancellationToken)).ToList();
        await Task.WhenAll(agents).ConfigureAwait(false);
    }

    /// <summary>
    /// Keep one agent alive. Backoff doubles per consecutive failure and resets once an agent has
    /// run for a while: a process that dies immediately ten times is broken, whereas one that ran
    /// for an hour and then dropped is an ordinary disconnect and should not inherit the old delay.
    /// </summary>
    private async Task SuperviseAsync(
        AgentConfig agentConfig, Func<IBanterClientTransport> transport, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(config.Restart.InitialDelaySeconds);
        var attempts = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                await using var agent = factory(agentConfig);
                await agent.StartAsync(transport(), cancellationToken).ConfigureAwait(false);

                attempts = 0;
                delay = TimeSpan.FromSeconds(config.Restart.InitialDelaySeconds);
                Reported?.Invoke($"{agentConfig.User} is in {string.Join(", ", agentConfig.Rooms)}");

                await agent.RunAsync(cancellationToken).ConfigureAwait(false);
                return;   // clean shutdown
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // An agent that ran for a decent stretch before failing is not in a crash loop.
                if (DateTimeOffset.UtcNow - startedAt > TimeSpan.FromMinutes(1))
                {
                    attempts = 0;
                    delay = TimeSpan.FromSeconds(config.Restart.InitialDelaySeconds);
                }

                attempts++;
                Reported?.Invoke($"{agentConfig.User} stopped: {ex.Message}");

                if (!config.Restart.Enabled)
                {
                    return;
                }

                if (attempts >= config.Restart.MaxAttempts)
                {
                    // Stop and say so. A fleet that silently retries forever looks healthy while
                    // one of its members has been absent all day.
                    Reported?.Invoke(
                        $"{agentConfig.User} failed {attempts} times running - giving up on it. " +
                        "The rest of the fleet keeps running.");
                    return;
                }

                Reported?.Invoke($"{agentConfig.User} restarting in {delay.TotalSeconds:0}s (attempt {attempts})");
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromSeconds(
                    Math.Min(delay.TotalSeconds * 2, config.Restart.MaxDelaySeconds));
            }
        }
    }

    /// <summary>
    /// Build the SDK options for one configured agent. Secrets come from the environment, never
    /// from the config file.
    /// </summary>
    public static (BanterAgentOptions Agent, LlmChatAgentOptions Llm) BuildOptions(
        FleetConfig fleet, AgentConfig agent)
    {
        var password = Environment.GetEnvironmentVariable(agent.ResolvedPasswordEnv)
            ?? throw new InvalidOperationException(
                $"'{agent.User}' has no password: set {agent.ResolvedPasswordEnv}.");

        var endpoint = agent.Llm ?? fleet.Llm;
        var apiKey = agent.ResolvedApiKeyEnv is { Length: > 0 } key
            ? Environment.GetEnvironmentVariable(key) ?? ""
            : "";

        var llm = new LlmChatAgentOptions
        {
            Endpoint = new Uri(endpoint),
            Model = agent.Model,
            ApiKey = apiKey,
        };

        if (agent.System is { Length: > 0 } prompt)
        {
            llm = llm with { SystemPrompt = prompt };
        }

        var options = new BanterAgentOptions
        {
            Server = new Uri(fleet.Server),
            User = agent.User,
            Password = password,
            Rooms = agent.Rooms,
            ClientName = "Banter.Warden",
            RespondToEveryMessage = agent.AnswerAll,
            Locality = agent.Locality,
            Clearance = agent.Clearance,
            Skills = agent.Skills,
            Description = $"{agent.Model} via {endpoint}",
            CostTier = agent.Cost,
            WantsDelegator = agent.Delegator,
            Routing = agent.Route
                ? new RoutingOptions
                {
                    AllowFrontier = !agent.NoFrontier,
                    Classifier = agent.LlmClassify
                        ? new LlmRequestClassifier(new OpenAiChatClient(llm with
                        {
                            // Classification gates every message, so a slow one stalls the room.
                            Timeout = TimeSpan.FromSeconds(45),
                            MaxOutputTokens = 200,
                            Temperature = 0,
                        }))
                        : new Core.KeywordRequestClassifier(),
                }
                : null,
            TaskWork = agent.WorkTasks
                ? new TaskWorkOptions { ClaimOpenTasks = !agent.AssignedOnly }
                : null,
        };

        return (options, llm);
    }
}
