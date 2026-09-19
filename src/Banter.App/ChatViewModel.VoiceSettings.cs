using System.Globalization;
namespace Banter.App;

/// <summary>
/// The voice half of the settings page: what this machine hears with, what it speaks with, and
/// who sounds like what.
///
/// <para><b>All of it is a client setting, deliberately.</b> Which engine transcribes, which
/// server speaks, and which voice an agent is heard in are properties of this machine and the ears
/// in front of it — not of the room. The voice pool differs between a Piper install and a Qwen
/// one; somebody running no speech server has no opinion to store; and two people in the same room
/// can reasonably want the same agent to sound different. Putting any of it on the agent identity
/// would make an operator decide what everyone hears, force one shared pool on every listener, and
/// add a protocol round trip to a preference that never leaves this machine.</para>
///
/// <para>Nothing here touches an audio device — the view model reflects what the head wired and
/// records what was chosen, which is what keeps all of it testable without a microphone.</para>
/// </summary>
public sealed partial class ChatViewModel
{
    private IReadOnlyList<(string Id, string Label)> _voicePool = [];
    private readonly Dictionary<string, string> _voicePins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Everyone this client has heard from, so the voice list is who you actually talk to rather
    /// than a directory. Kept across rooms: a voice belongs to a speaker, not to a room.
    /// </summary>
    private readonly SortedSet<string> _speakers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fills the page from what the head loaded and from what it can actually speak with.</summary>
    public void SetVoiceSettings(
        string engine, string language, string vocabulary,
        string endpoint, string wyomingTts,
        IReadOnlyList<(string Id, string Label)> pool,
        IReadOnlyDictionary<string, string> pins)
    {
        Model.VoiceLanguage = language;
        Model.VoiceVocabulary = vocabulary;
        Model.VoiceEndpoint = endpoint;
        Model.VoiceWyomingTts = wyomingTts;
        Model.TranscribeChoices = TranscribeChoices(engine);

        _voicePool = pool;
        _voicePins.Clear();
        foreach (var (nick, voice) in pins)
        {
            _voicePins[nick] = voice;
        }

        // Nothing on this machine speaks: leave the controls out rather than showing an empty
        // picker, which reads as a list that failed to load.
        RefreshSpeakerVoices();

        // Through ShowSettingsSection, so the pool and the chosen section are applied together.
        ShowSettingsSection(SettingsSection);
    }

    /// <summary>Notes somebody worth being able to give a voice to.</summary>
    public void SawSpeaker(string nick)
    {
        if (nick.Length == 0 || nick == "*" || IsSelf(nick))
        {
            return;
        }

        if (_speakers.Add(nick))
        {
            RefreshSpeakerVoices();
        }
    }

    /// <summary>Pins a speaker's voice, or clears it when <paramref name="voiceId"/> is null.</summary>
    public void PinVoice(string nick, string? voiceId)
    {
        if (voiceId is { Length: > 0 })
        {
            _voicePins[nick] = voiceId;
        }
        else
        {
            _voicePins.Remove(nick);
        }

        RefreshSpeakerVoices();
    }

    /// <summary>
    /// The next voice for a speaker: unpinned, then each voice in turn, then unpinned again.
    /// A cycle rather than a dropdown because the engine has no dropdown, and because a pool of
    /// six is short enough to walk. Ending back at unpinned means "let it choose for me" is always
    /// reachable without hunting for a separate clear.
    /// </summary>
    public string? CycleVoice(string nick)
    {
        if (_voicePool.Count == 0)
        {
            return null;
        }

        if (!_voicePins.TryGetValue(nick, out var current))
        {
            return _voicePool[0].Id;
        }

        var at = _voicePool.ToList().FindIndex(v => v.Id == current);
        return at < 0 || at + 1 >= _voicePool.Count ? null : _voicePool[at + 1].Id;
    }

    /// <summary>Who has been given a voice on purpose, for the head to save.</summary>
    public IReadOnlyDictionary<string, string> VoicePins => _voicePins;

    /// <summary>The transcription engine as the page has it, for the head to save.</summary>
    public string ChosenTranscribe => Chosen(Model.TranscribeChoices);

    public void ChooseTranscribe(string value) =>
        Model.TranscribeChoices = TranscribeChoices(value);

    private void RefreshSpeakerVoices() =>
        Model.SpeakerVoices = [.. _speakers.Select(nick =>
        {
            var pinned = _voicePins.TryGetValue(nick, out var id) ? id : null;
            var label = pinned is null
                ? "dealt by name"
                : _voicePool.FirstOrDefault(v => v.Id == pinned).Label ?? pinned;

            return new SpeakerVoiceRow
            {
                Nick = nick,
                Initials = InitialsOf(nick),
                VoiceLabel = label,
                // In words rather than only in colour: which of the two states a row is in is the
                // entire content of the row.
                VoiceKind = pinned is null ? "automatic" : "chosen",
                RowClass = pinned is null ? "mgmt-row" : "mgmt-row selected",
            };
        })];

    /// <summary>
    /// What listens. On this machine is private and needs no server at all, which is why it is the
    /// default; the other two are for a machine that would rather not run a model itself.
    /// </summary>
    /// <summary>
    /// Fills the auto-submit controls. Separate from <see cref="SetVoiceSettings"/> because it is
    /// about what happens to a transcript rather than about how one is made, and a head can have
    /// an opinion on one without the other.
    /// </summary>
    public void SetAutoSubmit(bool autoSubmit, double delaySeconds)
    {
        AutoSubmit = autoSubmit;
        AutoSubmitDelaySeconds = delaySeconds;
        Model.AutoSubmitChoices = AutoSubmitChoices(autoSubmit ? "send" : "hold");
        Model.AutoSubmitDelay = delaySeconds.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Whether the settings page currently says a transcript should send itself.</summary>
    public bool ChosenAutoSubmit => Chosen(Model.AutoSubmitChoices) == "send";

    public void ChooseAutoSubmit(string value)
    {
        AutoSubmit = value == "send";
        Model.AutoSubmitChoices = AutoSubmitChoices(AutoSubmit ? "send" : "hold");

        // A countdown already running belongs to the old setting.
        if (!AutoSubmit)
        {
            CancelPendingSubmit();
        }
    }

    /// <summary>
    /// Reads the delay box. Anything unparseable is left at what it was rather than becoming
    /// zero: a typo in a text field must not turn "wait three seconds" into "send immediately",
    /// which is the one direction of this setting that cannot be undone.
    /// </summary>
    public double ReadAutoSubmitDelay()
    {
        var typed = Model.AutoSubmitDelay.Trim();
        if (double.TryParse(typed, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0 && seconds <= 60)
        {
            AutoSubmitDelaySeconds = seconds;
        }

        Model.AutoSubmitDelay =
            AutoSubmitDelaySeconds.ToString("0.##", CultureInfo.InvariantCulture);
        return AutoSubmitDelaySeconds;
    }

    /// <summary>Which group of settings is on screen. Only one is, because only one fits.</summary>
    public string SettingsSection { get; private set; } = "you";

    /// <summary>
    /// Shows one section and hides the rest.
    ///
    /// <para>The speakers field is decided here too rather than by its own rule: it is hidden
    /// when this machine can speak with nothing, and hidden when its section is not showing, and
    /// two separate things setting one class is how a field ends up visible for one reason and
    /// invisible for another.</para>
    /// </summary>
    public void ShowSettingsSection(string section)
    {
        SettingsSection = section;
        Model.SettingsSections = SectionChoices(section);
        Model.YouFieldsClass = FieldClass(section == "you");
        Model.AlertFieldsClass = FieldClass(section == "alerts");
        Model.ListeningFieldsClass = FieldClass(section == "listening");
        Model.TranscriptFieldsClass = FieldClass(section == "transcripts");
        Model.VoiceSpeakersClass = FieldClass(section == "speaking" && _voicePool.Count > 0);
        Model.StayConnectedClass = FieldClass(section == "alerts" && CanStayConnected);
    }

    private static string FieldClass(bool shown) => shown ? "mgmt-field" : "mgmt-field hidden";

    /// <summary>
    /// The tabs. Plain rows rather than the radio-style cards the rest of the page uses: these
    /// choose what you are looking at, not what the application will do, and dressing a view
    /// switch as a setting invites people to wonder what they just changed.
    /// </summary>
    private static List<ChoiceRow> SectionChoices(string selected) =>
    [
        .. new[]
        {
            ("you", "You"),
            ("alerts", "Alerts"),
            ("listening", "Listening"),
            ("transcripts", "Transcripts"),
            ("speaking", "Speaking"),
        }.Select(o => new ChoiceRow
        {
            Value = o.Item1,
            Label = o.Item2,
            RowClass = o.Item1 == selected ? "settings-tab on" : "settings-tab",
        }),
    ];

    /// <summary>Whether being named should ask for attention outside the window.</summary>
    public bool FlashOnMention { get; private set; } = true;

    /// <summary>
    /// Whether being named is announced by a system notification rather than by asking a window
    /// manager for attention. Its own flag rather than a second meaning for
    /// <see cref="CanStayConnected"/>: they happen to be true on the same head today, and one
    /// value standing for two facts is how a setting ends up describing something it does not do.
    /// </summary>
    public bool AlertsAreNotifications { get; private set; }

    /// <summary>Called by a head whose platform has a notification shade.</summary>
    public void EnableNotificationAlerts()
    {
        AlertsAreNotifications = true;
        SetFlashOnMention(FlashOnMention);
    }

    public void SetFlashOnMention(bool flash)
    {
        FlashOnMention = flash;
        Model.FlashChoices = AlertsAreNotifications
            ? NotifyChoices(flash ? "flash" : "quiet")
            : FlashChoices(flash ? "flash" : "quiet");
    }

    public void ChooseFlash(string value) => SetFlashOnMention(value == "flash");

    /// <summary>
    /// Whether this host can hold a connection open while it is in the background at all.
    ///
    /// <para>False everywhere but Android, and not because the others lack the feature — because
    /// they lack the problem. Nothing takes a desktop's sockets away while it is not in front, so
    /// a control offering to prevent that would be a setting that does nothing, which is worse
    /// than one that is absent.</para>
    /// </summary>
    public bool CanStayConnected { get; private set; }

    /// <summary>Called by the head that has the problem. Nothing else calls it.</summary>
    public void EnableStayConnected(bool on)
    {
        CanStayConnected = true;
        SetStayConnected(on);
    }

    /// <summary>Whether to hold the connection open in the background. See
    /// <c>BanterSettings.StayConnected</c> for what it costs and why it is off by default.</summary>
    public bool StayConnected { get; private set; }

    public void SetStayConnected(bool stay)
    {
        StayConnected = stay;
        // Empty when this host cannot do it, so no row exists to be clicked or tabbed to. A
        // control hidden by a class is still a control, which the scan button taught us.
        Model.StayChoices = CanStayConnected
            ? StayChoices(stay ? "stay" : "drop")
            : [];
    }

    public void ChooseStay(string value) => SetStayConnected(value == "stay");

    private static List<ChoiceRow> StayChoices(string selected) => Choices(selected,
        ("drop", "Only while open", "Disconnects when you put the phone down, and reconnects when you come back."),
        ("stay", "Stay connected", "Keeps the connection while Banter is in the background, with a notification saying so. Android ends it after about six hours a day."));

    /// <summary>True when the interface should be sized for a finger. See ChatModel.PointerChoices
    /// for why this is a setting and not something the window width can answer.</summary>
    public bool TouchLayout { get; private set; }

    public void SetTouchLayout(bool touch)
    {
        TouchLayout = touch;
        Model.PointerChoices = PointerChoices(touch ? "touch" : "mouse");
    }

    public void ChoosePointer(string value) => SetTouchLayout(value == "touch");

    private static List<ChoiceRow> PointerChoices(string selected) => Choices(selected,
        ("mouse", "Mouse or trackpad", "Controls sized for a cursor."),
        ("touch", "Touch", "Everything you can tap becomes at least 44 across. What a phone gets, and what a touchscreen laptop wants."));

    private static List<ChoiceRow> FlashChoices(string selected) => Choices(selected,
        ("flash", "Flash the taskbar", "Only when the window is not already in front, and only for an explicit @name."),
        ("quiet", "Nothing", "The message is still highlighted in the room."));

    /// <summary>
    /// The same choice on a device that has no taskbar to flash. Worth its own wording rather than
    /// a vaguer one that covers both: "flash the taskbar" on a phone describes nothing the phone
    /// does, and a setting nobody can picture is one nobody touches.
    /// </summary>
    private static List<ChoiceRow> NotifyChoices(string selected) => Choices(selected,
        ("flash", "Notify me", "Only while you are looking at something else, and only for an explicit @name."),
        ("quiet", "Nothing", "The message is still highlighted in the room."));

    private static List<ChoiceRow> AutoSubmitChoices(string selected) => Choices(selected,
        ("send", "Send it", "A finished transcript posts itself, after the wait below."),
        ("hold", "Leave it in the composer", "Nothing is sent until you press Enter."));

    private static List<ChoiceRow> TranscribeChoices(string selected) => Choices(selected.ToLowerInvariant(),
        ("local", "On this machine", "Whisper, running here. Nothing spoken leaves this machine."),
        ("wyoming", "Wyoming", "A speech service you host, over the Wyoming protocol."),
        ("remote", "OpenAI-compatible", "An endpoint that speaks the OpenAI audio API."));
}
