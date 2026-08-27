using Banter.Voice;

namespace Banter.App;

/// <summary>
/// The client's voice half (PLAN §6): what a transcript does when it arrives, and what an
/// incoming message does on the way to the speaker.
///
/// <para>Optional throughout. A head that wired no audio simply never calls
/// <see cref="AttachVoice"/>, and every method here is a no-op — which is why the desktop head can
/// gain a microphone without the web head growing one.</para>
/// </summary>
public sealed partial class BanterChatSession
{
    private VoiceSession? _voice;
    private ReadbackSession? _readback;

    /// <summary>Whether the microphone is open, so the toggle knows which way to go.</summary>
    public bool VoiceOpen { get; private set; }

    /// <summary>
    /// Wires a microphone, a speaker, or both. Either may be null: a machine with no microphone
    /// can still be read aloud to, and a silent one can still dictate.
    /// </summary>
    public void AttachVoice(VoiceSession? voice, ReadbackSession? readback)
    {
        _voice = voice;
        _readback = readback;

        if (voice is not null)
        {
            voice.DraftReady += OnDraft;
            voice.StateChanged += OnVoiceState;
            voice.Failed += OnVoiceFailed;
        }

        if (readback is not null)
        {
            readback.Failed += OnVoiceFailed;
            readback.Policy = _vm.Readback;
        }

        _vm.Post(() => _vm.EnableVoice(readbackAvailable: readback is not null));
    }

    /// <summary>Opens or closes the microphone, reporting a device that refused rather than hiding it.</summary>
    public async Task SetVoiceOpenAsync(bool open, CancellationToken cancellationToken = default)
    {
        if (_voice is not { } voice)
        {
            return;
        }

        try
        {
            if (open)
            {
                await voice.StartAsync(cancellationToken).ConfigureAwait(false);
                VoiceOpen = true;

                // Barge-in: the moment the user starts talking, the room stops talking over them.
                if (_readback is { } readback)
                {
                    await readback.SilenceAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await voice.StopAsync(cancellationToken).ConfigureAwait(false);
                VoiceOpen = false;
            }
        }
        catch (Exception ex)
        {
            VoiceOpen = false;
            _vm.Post(() =>
            {
                _vm.SetVoiceState(VoiceSessionState.Idle);
                _vm.VoiceFailed(ex.Message);
            });
        }
    }

    /// <summary>Applies a readback policy the user just chose.</summary>
    public async Task SetReadbackAsync(ReadbackPolicy policy, CancellationToken cancellationToken = default)
    {
        if (_readback is not { } readback)
        {
            return;
        }

        readback.Policy = policy;
        if (policy == ReadbackPolicy.Off)
        {
            // Turning speech off has to stop the sentence already playing, not just the next one.
            await readback.SilenceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A transcript. Sent straight to the active room unless the user asked to review first, in
    /// which case it lands in the composer and this does nothing else.
    /// </summary>
    private void OnDraft(VoiceDraft draft) =>
        _vm.Post(() =>
        {
            var room = _vm.Model.ActiveRoom;
            var text = _vm.AcceptDraft(draft.Text);
            if (text.Length == 0 || room.Length == 0)
            {
                return;
            }

            _ = SendDictatedAsync(room, text);
        });

    private async Task SendDictatedAsync(string room, string text)
    {
        try
        {
            await _client.SendMessageAsync(room, text).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Put it back in the composer rather than losing it: the user said this out loud and
            // has no copy of it anywhere else.
            _vm.Post(() =>
            {
                _vm.Model.Composer = _vm.Model.Composer.Length == 0 ? text : $"{_vm.Model.Composer} {text}";
                _vm.VoiceFailed($"could not send: {ex.Message}");
            });
        }
    }

    private void OnVoiceState(VoiceSessionState state) => _vm.Post(() => _vm.SetVoiceState(state));

    private void OnVoiceFailed(VoiceSessionError error) => _vm.Post(() => _vm.VoiceFailed(error.Message));

    /// <summary>Reads an incoming message aloud, if the policy says so. Called from the handlers
    /// in the main partial, which already run off the render thread.</summary>
    private void SpeakIncoming(string room, string sender, string text)
    {
        if (_readback is not { } readback || text.Length == 0)
        {
            return;
        }

        _vm.Post(() =>
        {
            // Only the room being looked at. Reading three rooms at once is unfollowable, and a
            // background room's traffic is not what the user is listening for.
            if (!string.Equals(room, _vm.Model.ActiveRoom, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            readback.Speak(sender, text, _vm.IsAgent(sender), _vm.IsSelf(sender));
        });
    }

    /// <summary>
    /// Who is behind a stream in flight. A delta carries only its stream id, and the readback
    /// queue needs the sender to pick a voice and to apply the policy.
    /// </summary>
    private readonly Dictionary<string, string> _speaking = [];

    private void BeginSpokenStream(string room, string streamId, string sender)
    {
        if (_readback is null)
        {
            return;
        }

        _vm.Post(() =>
        {
            if (string.Equals(room, _vm.Model.ActiveRoom, StringComparison.OrdinalIgnoreCase))
            {
                _speaking[streamId] = sender;
            }
        });
    }

    private void SpeakDelta(string streamId, string delta) =>
        _vm.Post(() =>
        {
            if (_readback is { } readback && _speaking.TryGetValue(streamId, out var sender))
            {
                readback.AppendDelta(sender, delta, _vm.IsAgent(sender), _vm.IsSelf(sender));
            }
        });

    private void EndSpokenStream(string streamId) =>
        _vm.Post(() =>
        {
            if (_speaking.Remove(streamId, out var sender))
            {
                _readback?.EndStream(sender);
            }
        });
}
