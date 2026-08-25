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

        var reply = new System.Text.StringBuilder();
        await foreach (var delta in _client.StreamAsync(messages, cancellationToken).ConfigureAwait(false))
        {
            reply.Append(delta);
            yield return delta;
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
