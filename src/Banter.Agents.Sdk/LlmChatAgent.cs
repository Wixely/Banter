using Banter.Protocol;

namespace Banter.Agents.Sdk;

/// <summary>
/// A room participant backed by any OpenAI-compatible endpoint: system prompt, a rolling
/// per-room context window, and replies streamed token by token as <c>MSG_STREAM_*</c>.
///
/// <para>No code required to run one — this is the "personality in a room" case from PLAN §Path B.
/// Context is kept per room, so the same agent in two rooms holds two conversations and cannot
/// leak one into the other.</para>
/// </summary>
public sealed class LlmChatAgent : BanterAgent
{
    private readonly LlmChatAgentOptions _llm;
    private readonly OpenAiChatClient _client;

    /// <summary>Rolling context per room. Guarded because turns run off the receive loop.</summary>
    private readonly Dictionary<string, List<ChatTurn>> _context = [];
    private readonly Lock _contextLock = new();

    public LlmChatAgent(BanterAgentOptions agent, LlmChatAgentOptions llm, HttpMessageHandler? handler = null)
        : base(agent)
    {
        _llm = llm;
        _client = new OpenAiChatClient(llm, handler);
    }

    /// <summary>
    /// Observe a room message without replying to it, so the agent's context includes the
    /// conversation it was not addressed in. Called for every message, answered or not.
    /// </summary>
    public void Observe(string room, string sender, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        lock (_contextLock)
        {
            var turns = _context.TryGetValue(room, out var existing) ? existing : _context[room] = [];
            // Other people's lines are 'user' turns tagged with who said them; a group chat has
            // more than one human, and an untagged transcript makes the model lose track of who
            // it is answering.
            turns.Add(string.Equals(sender, Nick, StringComparison.OrdinalIgnoreCase)
                ? ChatTurn.Assistant(text)
                : ChatTurn.User($"{sender}: {text}"));

            Trim(turns);
        }
    }

    protected override bool ShouldRespond(MsgPayload m)
    {
        // Record first: context must include messages that did not trigger a turn.
        Observe(m.Room, m.Sender, m.Text);
        return base.ShouldRespond(m);
    }

    protected override async IAsyncEnumerable<string> RespondAsync(
        string room, string sender, string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<ChatTurn> messages;
        lock (_contextLock)
        {
            messages = [ChatTurn.System(_llm.SystemPrompt)];
            if (_context.TryGetValue(room, out var turns))
            {
                messages.AddRange(turns);
            }
        }

        // The catalogue the server granted this agent. Empty on a server with no tool backend,
        // which collapses the loop below to exactly the one-shot stream it used to be.
        var specs = Tools.Select(t => new ToolSpec(t.Name, t.Description, t.Schema)).ToList();

        var reply = new System.Text.StringBuilder();
        for (var round = 1; ; round++)
        {
            var calls = new List<ToolCallRequest>();
            var said = new System.Text.StringBuilder();

            await foreach (var delta in _client.StreamAsync(messages, specs, calls, cancellationToken)
                               .ConfigureAwait(false))
            {
                // Yielded as it arrives even mid-tool-loop: a model that says "let me check" and
                // then goes quiet for thirty seconds reads as a broken agent, not a busy one.
                said.Append(delta);
                reply.Append(delta);
                yield return delta;
            }

            if (calls.Count == 0)
            {
                break;
            }

            if (round >= _llm.MaxToolRounds)
            {
                // Say so in the room rather than truncating silently: an answer that stopped
                // short of the tools it wanted is a different thing from a finished one.
                var note = $"\n(stopped after {_llm.MaxToolRounds} rounds of tool calls)";
                reply.Append(note);
                yield return note;
                break;
            }

            messages.Add(ChatTurn.AssistantCalls(said.ToString(), calls));
            foreach (var call in calls)
            {
                // The server runs it. A refusal comes back as an ordinary error result, which the
                // model can read and work around — it must not look like the tool crashed.
                var result = await CallToolAsync(call.Name, call.Arguments, room, cancellationToken)
                    .ConfigureAwait(false);
                messages.Add(ChatTurn.Tool(call.Id, result.Content));
            }
        }

        // Record what we actually said, so the next turn sees it. Observe() would also catch this
        // from the server's echo, but recording here keeps the context correct even if the echo
        // is delayed behind another message.
        if (reply.Length > 0)
        {
            lock (_contextLock)
            {
                var turns = _context.TryGetValue(room, out var existing) ? existing : _context[room] = [];
                turns.Add(ChatTurn.Assistant(reply.ToString()));
                Trim(turns);
            }
        }
    }

    private void Trim(List<ChatTurn> turns)
    {
        if (turns.Count > _llm.ContextMessages)
        {
            turns.RemoveRange(0, turns.Count - _llm.ContextMessages);
        }
    }

    /// <summary>Current context for a room — exposed for tests and supervision.</summary>
    public IReadOnlyList<ChatTurn> ContextFor(string room)
    {
        lock (_contextLock)
        {
            return _context.TryGetValue(room, out var turns) ? [.. turns] : [];
        }
    }
}
