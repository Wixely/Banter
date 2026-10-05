using Banter.Server.Persistence;

namespace Banter.Server.Tools;

/// <summary>
/// How a head gets its tools: one call, so there is only one of these to keep right.
///
/// <para>It exists because there is more than one Banter server. The TCP server wired the broker
/// up by hand, the mesh server never did, and the mesh server therefore ran with
/// <c>tools: null</c> — every agent on it saw no tools at all no matter what the config said or
/// what MCPHub offered, and the room reported nothing wrong because nothing was wrong as far as it
/// knew. A head that forgets this call still gets no tools, but it is now one line to forget
/// rather than thirty to copy, and both heads say the same things about the same deployment.</para>
///
/// <para>The caller owns the broker: it is the thing holding the upstream connections, so it has to
/// be disposed with the process rather than here. <c>await using</c> at the call site.</para>
/// </summary>
public static class ToolHost
{
    /// <summary>
    /// Where the upstream list is read from when nobody said: the flag, then the environment, then
    /// a file beside the process. A container mounts the config and sets the variable; a desktop
    /// run just drops an mcp.json next to the server.
    /// </summary>
    public static string ConfigPath(string? flag) =>
        flag ?? Environment.GetEnvironmentVariable("BANTER_MCP_CONFIG") ?? "mcp.json";

    /// <summary>
    /// Load the config, connect what it names, and say what happened. Returns a broker either way:
    /// with no upstreams configured it is a working broker that offers nothing, which is what a
    /// chat server without tools should be — not a startup failure (see <see cref="McpConfigFile"/>).
    /// </summary>
    public static async Task<McpToolBroker> StartAsync(
        BanterDatabase database,
        string? configFlag,
        CancellationToken cancellationToken = default)
    {
        var options = McpConfigFile.Load(ConfigPath(configFlag));
        var broker = new McpToolBroker(
            options, new ToolGrantStore(database), new HubIdentityStore(database));

        if (options.Upstreams.Count == 0)
        {
            return broker;
        }

        await broker.StartAsync(cancellationToken);

        // Connected, not merely registered: the registry keeps an entry for an upstream that
        // failed, so counting entries reported every deployment as fully connected — including one
        // that had just been refused for presenting no key.
        var connected = broker.Upstreams.Count(u => u.State == MCPHub.Proxy.UpstreamState.Connected);
        Console.WriteLine(
            $"MCP: {connected}/{options.Upstreams.Count} upstream(s) connected, " +
            $"{broker.AllTools().Count} tool(s) available to grant.");

        foreach (var failed in broker.Upstreams.Where(u => u.State != MCPHub.Proxy.UpstreamState.Connected))
        {
            Console.Error.WriteLine(
                $"mcp: '{failed.Key}' is {failed.State}"
                + (failed.LastError is { Length: > 0 } why ? $": {why}" : "."));
        }

        return broker;
    }

    /// <summary>
    /// "What can this agent actually see?" — answered without starting an agent, a room, or a
    /// client, and answered by whoever is authoritative rather than by this process guessing. For
    /// an upstream that enforces for itself, it opens that agent's own session and prints what the
    /// upstream offered it, which is also what the agent would get going there directly.
    ///
    /// <para>Shared between the heads because the question is about the deployment, not about the
    /// transport in front of it: the same agent on the same database must get the same answer from
    /// the mesh server as from the socket one.</para>
    /// </summary>
    public static async Task DescribeToolsForAsync(
        IToolBroker broker, string agent, CancellationToken cancellationToken = default)
    {
        var visible = await broker.ToolsForAsync(agent, cancellationToken);
        Console.WriteLine($"{agent}: {visible.Count} tool(s)");
        foreach (var group in visible.GroupBy(t => t.ServerKey).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {(group.Key.Length > 0 ? group.Key : "(unattributed)")}: "
                + string.Join(", ", group.Select(t => t.Name).Order(StringComparer.Ordinal)));
        }
    }
}
