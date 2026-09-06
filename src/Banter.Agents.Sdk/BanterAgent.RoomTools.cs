using System.Text.Json;
using Banter.Client.Core;
using Banter.Protocol;

namespace Banter.Agents.Sdk;

/// <summary>
/// The things a delegator can <em>do</em> to a room, offered to its model as tools.
///
/// <para>A delegator could already open a side room and move agents into it, but only as a fixed
/// reaction to a fan-out it had classified. That is enough for "these two should work on this
/// together" and not enough for anything else: it cannot decide to split work it was not asked to
/// split, and it cannot act on what a human tells it in the room. An agent that answers "yes, I'll
/// add scout" and then does nothing is worse than one that says it cannot.</para>
///
/// <para>So these are tools rather than reactions. They sit beside whatever MCP tools the server
/// granted (§8c) and go through the same loop, but they run here rather than on the tool broker —
/// they are actions on the conversation itself, not calls to an outside system.</para>
///
/// <para>Only a delegator is offered them. The server enforces that independently — moving agents
/// is delegator-only there too — but offering a tool that would be refused invites a model to keep
/// trying it, so the catalogue tells the truth about what this agent can do.</para>
/// </summary>
public abstract partial class BanterAgent
{
    /// <summary>One tool this agent runs itself.</summary>
    /// <param name="Name">What the model calls it.</param>
    /// <param name="Description">What it does, in the words the model will reason with.</param>
    /// <param name="Schema">JSON Schema for the arguments.</param>
    /// <param name="RunAsync">(room the call came from, raw JSON arguments) → what to tell the model.</param>
    protected sealed record LocalTool(
        string Name,
        string Description,
        string Schema,
        Func<string, JsonElement, CancellationToken, Task<string>> RunAsync);

    /// <summary>
    /// What this agent may call in <paramref name="room"/>. Two tiers, because they answer to
    /// different things: anyone may ask the room for a decision — a worker that hits something
    /// outside its brief is exactly who needs to — but only the delegator may rearrange the room,
    /// which is the same rule the server enforces.
    /// </summary>
    private IReadOnlyList<LocalTool> LocalToolsFor(string room)
    {
        if (!Options.RoomTools)
        {
            return [];
        }

        return IsDelegatorFor(room) ? [.. EveryoneTools, .. RoomTools] : EveryoneTools;
    }

    /// <summary>
    /// Everything this agent can call in <paramref name="room"/>: what the server granted, plus
    /// what it can do to the room itself.
    /// </summary>
    protected IReadOnlyList<ToolDescriptorPayload> ToolsFor(string room) =>
    [
        .. Tools,
        .. LocalToolsFor(room).Select(t => new ToolDescriptorPayload(t.Name, t.Description, t.Schema, "banter")),
    ];

    /// <summary>
    /// Runs a tool, whether it is ours or the server's. Local tools win on name, and there is
    /// exactly one namespace on purpose: the model should not have to know which kind it is
    /// calling, and a server tool called <c>open_side_room</c> would be a confusing thing to have.
    /// </summary>
    protected async Task<ToolResultPayload> InvokeToolAsync(
        string name, string arguments, string room, CancellationToken cancellationToken = default)
    {
        var local = LocalToolsFor(room).FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        if (local is null)
        {
            // Not ours, and not something the server granted either. Answered here rather than
            // let through: the server's refusal for an unknown name is NO_TOOLS, which reads as
            // "this server has no tools at all" and is the wrong thing to tell a model that asked
            // for one tool by name. Worse, it arrives as an exception and takes the whole turn
            // down with it — an agent that reaches for a tool it does not have should carry on
            // without it, not stop answering.
            if (!Tools.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                var offered = ToolsFor(room).Select(t => t.Name).ToList();
                return new ToolResultPayload(
                    name,
                    $"there is no tool called '{name}' here. " + (offered.Count == 0
                        ? "You have no tools in this room; answer without one."
                        : $"What you can call here: {string.Join(", ", offered)}."),
                    IsError: true);
            }

            try
            {
                return await CallToolAsync(name, arguments, room, cancellationToken).ConfigureAwait(false);
            }
            catch (BanterClientException ex)
            {
                return new ToolResultPayload(name, $"could not run that: {ex.Message}", IsError: true);
            }
        }

        try
        {
            using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            var said = await local.RunAsync(room, parsed.RootElement, cancellationToken).ConfigureAwait(false);
            return new ToolResultPayload(name, said, IsError: false);
        }
        catch (Exception ex) when (ex is JsonException or BanterClientException or InvalidOperationException)
        {
            // Handed back as a result rather than thrown, exactly as a server tool's refusal is:
            // the model can read it and try something else, and a failed room change must not look
            // like the agent crashed.
            return new ToolResultPayload(name, $"could not do that: {ex.Message}", IsError: true);
        }
    }

    /// <summary>Rearranging the room. Delegator only, matching what the server allows.</summary>
    protected virtual IReadOnlyList<LocalTool> RoomTools =>
    [
        new("open_side_room",
            "Open a side room off this one for a piece of work, and get its name back. Agents "
            + "invited into a side room may talk to each other there, which they cannot do in the "
            + "main channel. Use it when work needs two or more agents to confer. The side room "
            + "inherits this room's sensitivity, so nothing becomes more shareable by moving.",
            """
            {"type":"object","properties":{
              "purpose":{"type":"string","description":"What the room is for, in a few words. Becomes its name and topic."}
            },"required":["purpose"]}
            """,
            async (room, args, ct) =>
            {
                var purpose = Text(args, "purpose");
                var name = SubRoomName(room, purpose);
                await Client.CreateSubRoomAsync(name, room, Summarise(purpose), ct).ConfigureAwait(false);
                await Client.SetRoomModeAsync(name, RoomDispatchMode.Collaborate, ct).ConfigureAwait(false);
                return $"Opened {name}. Invite agents into it with invite_agent, then give each one "
                     + "its own instruction.";
            }),

        new("invite_agent",
            "Move an agent into a room and tell it what to do there, in one step. Give each agent "
            + "its own instruction naming its slice of the work: an agent that is told the whole "
            + "task does the whole task, and two of those duplicate each other.",
            """
            {"type":"object","properties":{
              "agent":{"type":"string","description":"The agent's nick."},
              "room":{"type":"string","description":"The room to move it into, as returned by open_side_room."},
              "instruction":{"type":"string","description":"What this agent specifically should do. One or two sentences."}
            },"required":["agent","room","instruction"]}
            """,
            async (_, args, ct) =>
            {
                var agent = Text(args, "agent");
                var target = Text(args, "room");
                var instruction = Text(args, "instruction");

                await Client.MoveAgentAsync(agent, target, "working this together", ct).ConfigureAwait(false);

                // Addressed by name, so the agent takes it as its own instruction rather than as
                // chatter it happens to be able to read.
                await Client.SendMessageAsync(target, $"@{agent} {instruction}", ct).ConfigureAwait(false);
                return $"{agent} is in {target} and has been told: {instruction}";
            }),

    ];

    /// <summary>What any agent may do, delegator or not.</summary>
    protected virtual IReadOnlyList<LocalTool> EveryoneTools =>
    [
        new("ask_operator",
            "Ask the people in this room a question and stop, when something needs a human "
            + "decision — adding an agent that is not here, spending money, anything outside what "
            + "you were asked to do. Say what you need and why. Do not act on the answer until "
            + "somebody gives one.",
            """
            {"type":"object","properties":{
              "question":{"type":"string","description":"What you need decided, and why it is needed."}
            },"required":["question"]}
            """,
            async (room, args, ct) =>
            {
                await Client.SendMessageAsync(room, $"[needs a decision] {Text(args, "question")}", ct)
                    .ConfigureAwait(false);
                return "Asked. Nobody has answered yet — wait for a reply in the room rather than "
                     + "assuming one, and do not do the thing you asked about in the meantime.";
            }),
    ];

    private static string Text(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : throw new InvalidOperationException($"'{name}' is required and must be a string.");
}
