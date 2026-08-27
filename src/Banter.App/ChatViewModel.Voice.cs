using Banter.Voice;

namespace Banter.App;

/// <summary>
/// The voice controls' state (PLAN §6). Nothing here touches a microphone: the view model only
/// ever reflects what a <see cref="VoiceSession"/> reports and decides what the user sees, which
/// is what keeps the whole of it testable without an audio device.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>
    /// What a finished transcript does. Review is the safe default for always-listening, where a
    /// misheard sentence would otherwise post itself; push-to-talk is deliberate enough to send.
    /// </summary>
    public bool ReviewBeforeSend { get; set; }

    /// <summary>Whether this client has a microphone wired at all.</summary>
    public bool VoiceAvailable { get; private set; }

    public ReadbackPolicy Readback { get; private set; } = ReadbackPolicy.AgentsOnly;

    /// <summary>Whether the microphone is open — what the button's second tap will close.</summary>
    public bool Listening { get; private set; }

    /// <summary>
    /// Turns the controls on. Called by a head that wired capture; without it the microphone and
    /// readback controls stay hidden rather than sitting there inert.
    /// </summary>
    public void EnableVoice(bool readbackAvailable)
    {
        VoiceAvailable = true;
        Model.MicClass = "mic";
        Model.ReadbackClass = readbackAvailable ? "readback" : "readback hidden";
        RefreshVoiceLabels();
    }

    /// <summary>Reflects what the session is doing. The one place the indicator is decided.</summary>
    public void SetVoiceState(VoiceSessionState state)
    {
        Listening = state is not VoiceSessionState.Idle;

        Model.MicClass = state switch
        {
            VoiceSessionState.Idle => "mic",
            VoiceSessionState.Listening => "mic armed",
            VoiceSessionState.Capturing => "mic hearing",
            VoiceSessionState.Transcribing => "mic working",
            _ => "mic",
        };

        Model.VoiceStatus = state switch
        {
            VoiceSessionState.Idle => "",
            VoiceSessionState.Listening => "Listening",
            VoiceSessionState.Capturing => "Hearing you",
            VoiceSessionState.Transcribing => "Transcribing",
            _ => "",
        };

        Model.MicText = Listening ? "Stop" : "Talk";
    }

    /// <summary>Cycles the readback policy — off, agents, everyone — which is what the toggle does.</summary>
    public ReadbackPolicy CycleReadback()
    {
        Readback = Readback switch
        {
            ReadbackPolicy.Off => ReadbackPolicy.AgentsOnly,
            ReadbackPolicy.AgentsOnly => ReadbackPolicy.Everyone,
            _ => ReadbackPolicy.Off,
        };

        RefreshVoiceLabels();
        return Readback;
    }

    public void SetReadback(ReadbackPolicy policy)
    {
        Readback = policy;
        RefreshVoiceLabels();
    }

    /// <summary>
    /// A transcript arrived. Returns the text to send, or empty when it went to the composer for
    /// the user to look at first — the caller sends what it is given and nothing else, so the
    /// review setting is honoured in exactly one place.
    /// </summary>
    public string AcceptDraft(string text)
    {
        var draft = text.Trim();
        if (draft.Length == 0)
        {
            return "";
        }

        if (!ReviewBeforeSend)
        {
            return draft;
        }

        // Appended rather than replacing: a half-typed message is not worth losing to a
        // transcript that arrived while it was being written.
        Model.Composer = Model.Composer.Length == 0 ? draft : $"{Model.Composer} {draft}";
        return "";
    }

    /// <summary>Something in the voice pipeline failed. Said in the timeline, where it is visible.</summary>
    public void VoiceFailed(string message) => System(Model.ActiveRoom, $"[voice] {message}");

    /// <summary>
    /// Whether a sender is an agent in the active room. Drives the readback policy, and answers
    /// false for anyone not in the roster — a human, or someone who has since left.
    /// </summary>
    public bool IsAgent(string sender) =>
        _agents.TryGetValue(Model.ActiveRoom, out var roster)
        && roster.Any(a => string.Equals(a.Nick, sender, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a sender is this user, under any of the names they appear as.</summary>
    public bool IsSelf(string sender) =>
        string.Equals(sender, Model.Nick, StringComparison.OrdinalIgnoreCase);

    private void RefreshVoiceLabels() =>
        Model.ReadbackText = Readback switch
        {
            ReadbackPolicy.Off => "Speech: off",
            ReadbackPolicy.AgentsOnly => "Speech: agents",
            _ => "Speech: everyone",
        };
}
