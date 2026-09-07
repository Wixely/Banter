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
    ///
    /// <para>Superseded by <see cref="AutoSubmit"/>. Setting it is still honoured, as "never send
    /// by itself", because it is in settings files that already exist.</para>
    /// </summary>
    public bool ReviewBeforeSend { get; set; }

    /// <summary>Whether a finished transcript sends itself, rather than waiting to be sent.</summary>
    public bool AutoSubmit { get; set; } = true;

    /// <summary>
    /// How long a transcript waits in the composer before sending itself. Zero, the default,
    /// sends at once - which is what this did before the wait was configurable.
    /// </summary>
    public double AutoSubmitDelaySeconds { get; set; }

    /// <summary>
    /// When the waiting transcript is due to send, or null when nothing is waiting. Held as an
    /// absolute instant rather than a countdown so nothing has to tick it: the frame loop asks
    /// whether it is due yet, and a dropped frame cannot make it late.
    /// </summary>
    private DateTimeOffset? _submitDue;

    /// <summary>What the composer held when the countdown started, so edits can cancel it.</summary>
    private string _submitText = "";

    /// <summary>Whether a transcript is counting down to send itself.</summary>
    public bool SubmitPending => _submitDue is not null;

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
        Model.VoiceRowClass = "voice-row";
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

        // Appended rather than replacing: a half-typed message is not worth losing to a
        // transcript that arrived while it was being written.
        Model.Composer = Model.Composer.Length == 0 ? draft : $"{Model.Composer} {draft}";

        // ReviewBeforeSend is the old switch and still means "never by itself".
        if (ReviewBeforeSend || !AutoSubmit)
        {
            CancelPendingSubmit();
            return "";
        }

        if (AutoSubmitDelaySeconds <= 0)
        {
            // Straight out, which is what this did before there was a delay to configure. The
            // composer is cleared here rather than by the caller, because from here on the text
            // exists only in the message being sent.
            var send = Model.Composer;
            Model.Composer = "";
            CancelPendingSubmit();
            return send;
        }

        StartPendingSubmit();
        return "";
    }

    /// <summary>
    /// Puts the composer on a countdown. Shown, and cancellable: recognition is wrong often
    /// enough that sending without a chance to stop it posts misheard sentences to a room.
    /// </summary>
    private void StartPendingSubmit()
    {
        _submitDue = Now() + TimeSpan.FromSeconds(AutoSubmitDelaySeconds);
        _submitText = Model.Composer;
        RefreshPendingSubmit();
    }

    /// <summary>
    /// Stops a pending send. Called by the cancel control, by typing into the composer, and by
    /// anything that sends or clears it — a countdown that outlives the text it was counting
    /// down for would send whatever happened to be in the box instead.
    /// </summary>
    public void CancelPendingSubmit()
    {
        _submitDue = null;
        _submitText = "";
        Model.PendingSubmitClass = "pending-submit hidden";
        Model.PendingSubmitText = "";
    }

    /// <summary>
    /// The text to send now, or empty. Asked once a frame by the head, which owns the clock.
    ///
    /// <para>Also the place a countdown is abandoned: if the composer no longer holds what the
    /// transcript put there, somebody has started editing, and editing means they intend to look
    /// at it rather than let it go.</para>
    /// </summary>
    public string TakeDueSubmission()
    {
        if (_submitDue is not { } due)
        {
            return "";
        }

        if (!string.Equals(Model.Composer, _submitText, StringComparison.Ordinal))
        {
            CancelPendingSubmit();
            return "";
        }

        if (Now() < due)
        {
            RefreshPendingSubmit();
            return "";
        }

        var send = Model.Composer;
        Model.Composer = "";
        CancelPendingSubmit();
        return send;
    }

    /// <summary>Sends the waiting transcript now rather than waiting the rest of the delay.</summary>
    public string TakePendingNow()
    {
        if (_submitDue is null)
        {
            return "";
        }

        var send = Model.Composer;
        Model.Composer = "";
        CancelPendingSubmit();
        return send;
    }

    private void RefreshPendingSubmit()
    {
        if (_submitDue is not { } due)
        {
            return;
        }

        // Rounded up, so a countdown never shows a zero it then sits on for most of a second.
        var left = Math.Max(0, (int)Math.Ceiling((due - Now()).TotalSeconds));
        Model.PendingSubmitClass = "pending-submit";
        Model.PendingSubmitText = $"Sending in {left}s";
    }

    /// <summary>
    /// The clock. Replaceable so the countdown can be tested without waiting real seconds for it,
    /// which is the difference between a test suite that covers this and one that skips it.
    /// Nothing else in the view model reads the time.
    /// </summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

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
