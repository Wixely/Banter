using Banter.Client.Core;

namespace Banter.App;

/// <summary>
/// Wires a live <see cref="BanterClient"/> to a <see cref="ChatViewModel"/>. Every handler does
/// nothing but <c>Post</c> a closure — no handler touches the model directly, because they all run
/// on the client's receive loop rather than the render thread.
///
/// <para>Kept separate from <see cref="BanterChatApp"/> so the app has no transport dependency and
/// the tests can exercise the timeline without a server.</para>
/// </summary>
public sealed class BanterChatSession : IDisposable
{
    private readonly BanterClient _client;
    private readonly ChatViewModel _vm;
    private bool _disposed;

    public BanterChatSession(BanterClient client, ChatViewModel viewModel)
    {
        _client = client;
        _vm = viewModel;

        _client.MessageReceived += OnMessage;
        _client.MemberJoined += OnJoined;
        _client.MemberParted += OnParted;
        _client.TopicChanged += OnTopic;
        _client.MessageStreamStarted += OnStreamStart;
        _client.MessageStreamDelta += OnStreamDelta;
        _client.MessageStreamEnded += OnStreamEnd;
        _client.PrivateMessageReceived += OnPrivate;
        _client.ServerError += OnServerError;
        _client.Disconnected += OnDisconnected;
        _client.Reconnecting += OnReconnecting;
        _client.Reconnected += OnReconnected;
    }

    /// <summary>Joins a room and back-fills it from server history so the timeline isn't empty.</summary>
    public async Task JoinAsync(string room, int history = 100, CancellationToken cancellationToken = default)
    {
        await _client.JoinAsync(room, cancellationToken).ConfigureAwait(false);
        _vm.Post(() =>
        {
            _vm.AddRoom(room);
            _vm.SetNick(_client.Nick);
        });

        var page = await _client.GetHistoryAsync(room, limit: history, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _vm.Post(() =>
        {
            foreach (var m in page.Messages)
            {
                _vm.Append(room, m.Sender, m.Text, m.Timestamp);
            }
        });
    }

    public Task PartAsync(string room, CancellationToken cancellationToken = default)
    {
        _vm.Post(() => _vm.RemoveRoom(room));
        return _client.PartAsync(room, cancellationToken: cancellationToken);
    }

    public Task SendAsync(string room, string text) =>
        _client.SendMessageAsync(room, text).AsTask();

    private void OnMessage(Protocol.MsgPayload m) =>
        _vm.Post(() => _vm.Append(m.Room, m.Sender, m.Text, m.Timestamp));

    private void OnJoined(Protocol.JoinPayload j) =>
        _vm.Post(() => _vm.System(j.Room, $"{j.Nick} joined"));

    private void OnParted(Protocol.PartPayload p) =>
        _vm.Post(() => _vm.System(p.Room, p.Reason is { Length: > 0 } r ? $"{p.Nick} left ({r})" : $"{p.Nick} left"));

    private void OnTopic(Protocol.TopicPayload t) =>
        _vm.Post(() =>
        {
            _vm.SetTopic(t.Room, t.Topic);
            _vm.System(t.Room, $"Topic: {t.Topic}");
        });

    private void OnStreamStart(Protocol.MsgStreamStartPayload s) =>
        _vm.Post(() => _vm.StreamStart(s.Room, s.Sender, s.StreamId));

    private void OnStreamDelta(Protocol.MsgStreamDeltaPayload d) =>
        _vm.Post(() => _vm.StreamDelta(d.StreamId, d.Delta));

    private void OnStreamEnd(Protocol.MsgStreamEndPayload e) =>
        _vm.Post(() => _vm.StreamEnd(e.StreamId, e.FinalText, e.Timestamp));

    private void OnPrivate(Protocol.PrivMsgPayload p) =>
        _vm.Post(() => _vm.System(_vm.Model.ActiveRoom, $"[pm] {p.Sender}: {p.Text}"));

    // Errors that match no outstanding request land here — including a refused send, which is
    // how an agent learns it has been throttled. Showing them in-timeline keeps that visible.
    private void OnServerError(Protocol.ErrorPayload e) =>
        _vm.Post(() => _vm.System(_vm.Model.ActiveRoom, $"server: {e.Code} {e.Message}"));

    private void OnDisconnected() =>
        _vm.Post(() => _vm.SetStatus("Disconnected", connected: false));

    private void OnReconnecting(int attempt) =>
        _vm.Post(() => _vm.SetStatus($"Reconnecting ({attempt})", connected: false));

    private void OnReconnected() =>
        _vm.Post(() => _vm.SetStatus("Connected", connected: true));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.MessageReceived -= OnMessage;
        _client.MemberJoined -= OnJoined;
        _client.MemberParted -= OnParted;
        _client.TopicChanged -= OnTopic;
        _client.MessageStreamStarted -= OnStreamStart;
        _client.MessageStreamDelta -= OnStreamDelta;
        _client.MessageStreamEnded -= OnStreamEnd;
        _client.PrivateMessageReceived -= OnPrivate;
        _client.ServerError -= OnServerError;
        _client.Disconnected -= OnDisconnected;
        _client.Reconnecting -= OnReconnecting;
        _client.Reconnected -= OnReconnected;
    }
}
