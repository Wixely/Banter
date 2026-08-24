using System.Text;

namespace Banter.Client.Core;

/// <summary>
/// A message being streamed into a room. Append deltas as they are produced (a model's tokens,
/// typically) then <see cref="CompleteAsync"/>; the server persists the final text as one
/// message and relays the authoritative end to every member.
/// </summary>
public sealed class BanterMessageStream(BanterClient client, string streamId) : IAsyncDisposable
{
    private readonly StringBuilder _accumulated = new();
    private bool _completed;

    public string StreamId { get; } = streamId;

    /// <summary>Everything appended so far — the fallback final text if the caller completes
    /// without supplying one.</summary>
    public string Text => _accumulated.ToString();

    public async ValueTask AppendAsync(string delta, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (delta.Length == 0)
        {
            return;
        }

        _accumulated.Append(delta);
        await client.SendStreamDeltaAsync(StreamId, delta, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ends the stream. <paramref name="finalText"/> overrides the accumulated text
    /// when the producer knows better (e.g. the model returned a cleaned-up final message).</summary>
    public async ValueTask CompleteAsync(string? finalText = null, CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        await client.SendStreamEndAsync(StreamId, finalText ?? Text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes the stream with whatever was accumulated, so an abandoned stream
    /// still resolves into a message rather than hanging open.</summary>
    public ValueTask DisposeAsync() => CompleteAsync();
}
