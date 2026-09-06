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
            "Ask the people in this room a question and WAIT for the answer, when something needs "
            + "a human decision - adding an agent that is not here, spending money, choosing "
            + "between approaches, anything outside what you were asked to do. Offer options where "
            + "there are sensible ones and they are shown as buttons to click; whoever answers can "
            + "always write something else instead. Returns what they chose. Ask more than one "
            + "question at once only when they are really one decision in parts.",
            """
            {"type":"object","properties":{
              "questions":{"type":"array","minItems":1,"maxItems":4,"items":{
                "type":"object","properties":{
                  "key":{"type":"string","description":"Short identifier for this question, so you can tell the answers apart."},
                  "header":{"type":"string","description":"Two or three words naming the decision, e.g. 'Extra agent'."},
                  "question":{"type":"string","description":"What you need decided, and why it is needed."},
                  "options":{"type":"array","items":{
                    "type":"object","properties":{
                      "value":{"type":"string","description":"What comes back if this is chosen."},
                      "label":{"type":"string","description":"What the person reads."},
                      "description":{"type":"string","description":"Why they would pick it."}
                    },"required":["value","label"]}},
                  "multi_select":{"type":"boolean","description":"Whether several options may be chosen at once."}
                },"required":["key","question"]}}
            },"required":["questions"]}
            """,
            async (room, args, ct) => await AskAndWaitAsync(room, args, ct).ConfigureAwait(false)),
    ];

    /// <summary>
    /// Puts the question in the room and waits for somebody to answer it.
    ///
    /// <para>Waiting is this agent's own turn, not the room's: everybody else carries on, other
    /// agents keep working, and the question sits on the message until it is answered. An agent
    /// that asked and then carried on regardless would be asking for form's sake.</para>
    ///
    /// <para>The wait is bounded. Nobody is obliged to answer, and an agent holding its turn open
    /// forever is one that has quietly stopped working - so a timeout hands the model back the
    /// fact that nobody replied, which is itself an answer of a kind.</para>
    /// </summary>
    private async Task<string> AskAndWaitAsync(string room, JsonElement args, CancellationToken cancellationToken)
    {
        var questions = ParseQuestions(args);
        var answered = new TaskCompletionSource<AnswerPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = await Client.AskAsync(room, questions, cancellationToken).ConfigureAwait(false);

        void OnAnswer(AnswerPayload a)
        {
            if (string.Equals(a.AskId, asked.AskId, StringComparison.Ordinal))
            {
                answered.TrySetResult(a);
            }
        }

        void OnClosed(AskClosedPayload c)
        {
            // Closed without reaching us. Ending the wait beats holding a turn open for something
            // that is never going to arrive.
            if (string.Equals(c.AskId, asked.AskId, StringComparison.Ordinal))
            {
                answered.TrySetResult(new AnswerPayload(asked.AskId, [], c.AnsweredBy));
            }
        }

        Client.AnswerReceived += OnAnswer;
        Client.AskClosed += OnClosed;
        try
        {
            var reply = await answered.Task.WaitAsync(Options.AskTimeout, cancellationToken).ConfigureAwait(false);
            return DescribeAnswer(questions, reply);
        }
        catch (TimeoutException)
        {
            return $"Nobody answered within {Options.AskTimeout.TotalMinutes:F0} minutes. Do not assume "
                 + "an answer: say in the room that you are still waiting, and stop rather than "
                 + "guessing at what was not decided.";
        }
        finally
        {
            Client.AnswerReceived -= OnAnswer;
            Client.AskClosed -= OnClosed;
        }
    }

    private static string DescribeAnswer(IReadOnlyList<AskQuestion> questions, AnswerPayload reply)
    {
        if (reply.Answers.Count == 0)
        {
            return "The question was closed without an answer.";
        }

        var said = reply.Answers.Select(a =>
        {
            var q = questions.FirstOrDefault(x => x.Key == a.Key);
            var chosen = a.Chosen
                .Select(v => q?.Options.FirstOrDefault(o => o.Value == v)?.Label ?? v)
                .ToList();

            var value = chosen.Count > 0 ? string.Join(", ", chosen) : "";
            if (a.Text.Length > 0)
            {
                // What somebody typed outranks what they clicked: they wrote it because the
                // buttons did not say what they meant.
                value = value.Length > 0 ? $"{value} (and said: {a.Text})" : a.Text;
            }

            return $"{a.Key} = {(value.Length > 0 ? value : "nothing chosen")}";
        });

        var who = reply.Responder.Length > 0 ? reply.Responder : "somebody";
        return $"{who} answered: {string.Join("; ", said)}";
    }

    private static IReadOnlyList<AskQuestion> ParseQuestions(JsonElement args)
    {
        if (!args.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("'questions' is required and must be an array.");
        }

        var parsed = list.EnumerateArray().Select(q => new AskQuestion(
            Key: Text(q, "key"),
            Header: Optional(q, "header") is { Length: > 0 } h ? h : Text(q, "key"),
            Text: Text(q, "question"),
            Options: q.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                ? [.. options.EnumerateArray().Select(o => new AskOption(
                    Text(o, "value"), Text(o, "label"), Optional(o, "description") ?? ""))]
                : [],
            MultiSelect: q.TryGetProperty("multi_select", out var multi) && multi.ValueKind == JsonValueKind.True))
            .ToList();

        return parsed.Count == 0
            ? throw new InvalidOperationException("Ask at least one question.")
            : parsed;
    }

    private static string? Optional(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Text(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : throw new InvalidOperationException($"'{name}' is required and must be a string.");
}
