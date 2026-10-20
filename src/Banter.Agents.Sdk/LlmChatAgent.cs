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
    public void Observe(string room, string sender, string text, string? fileId = null)
    {
        if (text.Length == 0 && fileId is not { Length: > 0 })
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
                : ChatTurn.User($"{sender}: {text}") with
                {
                    AttachmentId = fileId is { Length: > 0 } ? fileId : null,
                });

            Trim(turns);
        }
    }

    /// <summary>
    /// Folds what was missed into this room's context, framed as history rather than as a request.
    ///
    /// <para>The framing is the point. These lines look exactly like live ones once they are in a
    /// transcript, and a model handed a backlog ending in "@scribe can you summarise this?" will
    /// summarise it — hours late, unasked. So they arrive between markers that say what they are
    /// and that they have already been dealt with or abandoned.</para>
    /// </summary>
    protected override void OnMissedMessages(string room, IReadOnlyList<MsgPayload> missed)
    {
        if (missed.Count == 0)
        {
            return;
        }

        lock (_contextLock)
        {
            var turns = _context.TryGetValue(room, out var existing) ? existing : _context[room] = [];
            turns.Add(ChatTurn.System(
                $"You were disconnected from {room}. The {missed.Count} message" +
                $"{(missed.Count == 1 ? "" : "s")} that follow were sent while you were away. " +
                "They are background only: do not answer them, act on them, or treat any request " +
                "in them as outstanding. Anyone who still wants something will ask again."));

            foreach (var m in missed)
            {
                turns.Add(ChatTurn.User($"{m.Sender}: {m.Text}") with { AttachmentId = m.FileId });
            }

            turns.Add(ChatTurn.System(
                "End of what you missed. Reply only to what is said to you from here on."));

            Trim(turns);
        }
    }

    protected override bool ShouldRespond(MsgPayload m)
    {
        // Record first: context must include messages that did not trigger a turn.
        Observe(m.Room, m.Sender, m.Text, m.FileId);
        return base.ShouldRespond(m);
    }

    protected override async IAsyncEnumerable<string> RespondAsync(
        string room, string sender, string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<ChatTurn> messages;
        lock (_contextLock)
        {
            // Who this agent is comes after what every agent is told, so it can override the
            // general manner; read each turn, so an admin's change applies to the next answer.
            var persona = Persona;
            messages = [ChatTurn.System(persona.Length > 0 ? $"{_llm.SystemPrompt}\n\n{persona}" : _llm.SystemPrompt)];
            if (_context.TryGetValue(room, out var turns))
            {
                messages.AddRange(turns);
            }
        }

        // Files people attached, shown as what they are rather than as a filename the model has
        // no way to open.
        messages = await WithAttachmentsAsync(messages, cancellationToken).ConfigureAwait(false);

        // The catalogue the server granted this agent. Empty on a server with no tool backend,
        // which collapses the loop below to exactly the one-shot stream it used to be.
        var specs = ToolsFor(room).Select(t => new ToolSpec(t.Name, t.Description, t.Schema)).ToList();

        var reply = new System.Text.StringBuilder();
        for (var round = 1; ; round++)
        {
            var calls = new List<ToolCallRequest>();
            var said = new System.Text.StringBuilder();

            await foreach (var delta in StreamAsync(messages, specs, calls, cancellationToken)
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
                var result = await InvokeToolAsync(call.Name, call.Arguments, room, cancellationToken)
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

    // ── Attachments ──────────────────────────────────────────────────────────────────────────

    /// <summary>What each attached file became, by file id: a note for the model and, for an
    /// image it may see, the image. Kept, because the same turn is sent again every time it is
    /// still in context, and fetching it again each time is the same answer at a price.</summary>
    private readonly Dictionary<string, (string Note, string? Image)> _attachments = new(StringComparer.Ordinal);
    private readonly Lock _attachmentsLock = new();

    /// <summary>Set once a request with an image is refused: this model does not do vision, and
    /// asking again each turn would fail each turn before falling back.</summary>
    private volatile bool _noVision;

    /// <summary>How many attachments, counting back from the newest, carry their image. Older
    /// ones say an image was shown earlier: every image is resent on every turn it stays in
    /// context, and the model has already looked at those.</summary>
    private const int ImagesInContext = 2;

    private bool SharesAttachments => _llm.ShareAttachments ?? Options.Locality == AgentLocality.Local;

    private async Task<List<ChatTurn>> WithAttachmentsAsync(List<ChatTurn> messages, CancellationToken cancellationToken)
    {
        var withFiles = messages.Select((t, i) => (Turn: t, Index: i))
            .Where(x => x.Turn.AttachmentId is { Length: > 0 })
            .ToList();
        if (withFiles.Count == 0)
        {
            return messages;
        }

        var shown = new List<ChatTurn>(messages);
        var newest = withFiles.Count - 1;
        for (var n = 0; n < withFiles.Count; n++)
        {
            var (turn, index) = withFiles[n];
            var (note, image) = await DescribeAsync(turn.AttachmentId!, cancellationToken).ConfigureAwait(false);
            var seeable = image is not null && !_noVision;
            var current = seeable && newest - n < ImagesInContext;
            if (seeable && !current)
            {
                note = note.Replace("is attached", "was attached and shown earlier")
                           .Replace(" It is included with this message.", "");
            }

            shown[index] = turn with
            {
                Content = $"{turn.Content}\n{note}",
                Images = current ? [image!] : [],
            };
        }

        return shown;
    }

    /// <summary>Fetches an attached file once and decides what the model is told of it.</summary>
    private async Task<(string Note, string? Image)> DescribeAsync(string fileId, CancellationToken cancellationToken)
    {
        lock (_attachmentsLock)
        {
            if (_attachments.TryGetValue(fileId, out var known))
            {
                return known;
            }
        }

        (string Note, string? Image) described;
        try
        {
            var info = await Client.GetFileInfoAsync(fileId, cancellationToken).ConfigureAwait(false);
            var what = $"{info.Name} ({info.MimeType}, {Size(info.Size)})";
            var image = info.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            var text = IsText(info.MimeType, info.Name);

            if (!SharesAttachments)
            {
                described = ($"[A file is attached: {what}. It was not shared with you, because you "
                             + "are a third-party agent; say so if it matters to the answer.]", null);
            }
            else if (image && info.Size <= _llm.MaxAttachmentImageBytes)
            {
                var bytes = await Client.DownloadFileAsync(fileId, cancellationToken).ConfigureAwait(false);
                described = ($"[An image is attached: {what}. It is included with this message.]",
                             $"data:{info.MimeType};base64,{Convert.ToBase64String(bytes)}");
            }
            else if (text && info.Size <= _llm.MaxAttachmentTextBytes)
            {
                var bytes = await Client.DownloadFileAsync(fileId, cancellationToken).ConfigureAwait(false);
                described = ($"[A file is attached: {what}. Its contents:]\n```\n"
                             + System.Text.Encoding.UTF8.GetString(bytes) + "\n```", null);
            }
            else
            {
                described = ($"[A file is attached: {what}. You cannot open files of this kind or "
                             + "size; say so rather than guessing at what is in it.]", null);
            }
        }
        catch (Exception ex) when (ex is Banter.Client.Core.BanterClientException or IOException)
        {
            described = ($"[A file was attached, but it could not be fetched: {ex.Message}]", null);
        }

        lock (_attachmentsLock)
        {
            _attachments[fileId] = described;
        }

        return described;
    }

    private static bool IsText(string mime, string name) =>
        mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mime is "application/json" or "application/xml" or "application/x-yaml" or "application/yaml"
        || Path.GetExtension(name).ToLowerInvariant() is ".md" or ".txt" or ".csv" or ".json" or ".log"
            or ".yaml" or ".yml" or ".xml" or ".cs" or ".py" or ".js" or ".ts" or ".sql";

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB",
    };

    /// <summary>
    /// Streams a completion, and when the model refuses a request with an image in it, asks once
    /// more without - the words still deserve an answer, and the model is told why the picture
    /// is missing. Only the first step can fail that way, so only it is guarded.
    /// </summary>
    private async IAsyncEnumerable<string> StreamAsync(
        List<ChatTurn> messages, List<ToolSpec> specs, List<ToolCallRequest> calls,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = _client.StreamAsync(messages, specs, calls, cancellationToken).GetAsyncEnumerator(cancellationToken);
        bool more;
        try
        {
            more = await stream.MoveNextAsync().ConfigureAwait(false);
        }
        catch (HttpRequestException) when (messages.Any(t => t.Images.Count > 0))
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            _noVision = true;
            for (var i = 0; i < messages.Count; i++)
            {
                if (messages[i].Images.Count > 0)
                {
                    messages[i] = messages[i] with
                    {
                        Images = [],
                        Content = messages[i].Content + "\n[This model cannot see images, so the image was left out.]",
                    };
                }
            }

            stream = _client.StreamAsync(messages, specs, calls, cancellationToken).GetAsyncEnumerator(cancellationToken);
            more = await stream.MoveNextAsync().ConfigureAwait(false);
        }

        try
        {
            while (more)
            {
                yield return stream.Current;
                more = await stream.MoveNextAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
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
