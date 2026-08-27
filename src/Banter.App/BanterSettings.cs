using System.Text.Json;
using System.Text.Json.Serialization;

namespace Banter.App;

/// <summary>
/// Persisted client settings.
///
/// <para><b>Secrets are deliberately absent.</b> The account password and the CupriNet watchword
/// are not stored: this file is plain JSON in the user's profile with no OS-level protection, and
/// a chat client is not a credential manager. They come from <c>--pass</c> / the
/// <c>BANTER_PASS</c> and <c>BANTER_WATCHWORD</c> environment variables instead, so the choice of
/// where to keep them belongs to whatever the user already trusts. Adding "remember my password"
/// later means integrating a real keychain per platform, not adding a field here.</para>
/// </summary>
public sealed record BanterSettings
{
    /// <summary>Server URI. Scheme picks the transport: <c>tcp://</c> or a CupriNet link.</summary>
    public string Server { get; init; } = "";

    public string User { get; init; } = "";

    /// <summary>Rooms to join on connect, in order. The first becomes the active room.</summary>
    public IReadOnlyList<string> Rooms { get; init; } = [];

    /// <summary>Messages to request per history page.</summary>
    public int HistoryPageSize { get; init; } = 100;

    /// <summary>Messages kept per room before the oldest are dropped from memory.</summary>
    public int Scrollback { get; init; } = 5_000;

    /// <summary>
    /// Speech settings (PLAN §6). The API key is absent for the same reason the password is:
    /// it comes from <c>BANTER_SPEECH_KEY</c>.
    /// </summary>
    public VoiceSettings Voice { get; init; } = new();

    [JsonIgnore]
    public bool IsComplete =>
        Server.Length > 0 && User.Length > 0 && Uri.TryCreate(Server, UriKind.Absolute, out _);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Default location: <c>%APPDATA%/Banter/settings.json</c> on Windows, <c>~/.config</c>
    /// equivalent elsewhere — the same root the CupriNet identity uses, so a profile is one folder.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Banter", "settings.json");

    /// <summary>
    /// Load settings, or defaults when the file is missing or unreadable. A corrupt file is not
    /// fatal — a client that refuses to start because its preferences file is malformed is worse
    /// than one that starts with defaults — but the reason is reported through
    /// <paramref name="problem"/> so a caller can surface it.
    /// </summary>
    public static BanterSettings Load(string? path = null, Action<string>? problem = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return new BanterSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<BanterSettings>(File.ReadAllText(path), Json) ?? new BanterSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problem?.Invoke($"could not read {path}: {ex.Message}");
            return new BanterSettings();
        }
    }

    /// <summary>Write settings, creating the directory if needed. Returns false on failure.</summary>
    public bool TrySave(string? path = null, Action<string>? problem = null)
    {
        path ??= DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem?.Invoke($"could not write {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Overlay explicit command-line values on top of stored settings. Anything not supplied on
    /// the command line keeps its stored value, so <c>banter --room #other</c> is a valid way to
    /// start against the saved server and account.
    /// </summary>
    public BanterSettings With(string? server, string? user, IReadOnlyList<string>? rooms) => this with
    {
        Server = server ?? Server,
        User = user ?? User,
        Rooms = rooms is { Count: > 0 } ? rooms : Rooms,
    };
}

/// <summary>
/// How this client speaks and listens. Everything here is a preference rather than a credential,
/// which is why it can live in a plain JSON file next to the rest.
/// </summary>
public sealed record VoiceSettings
{
    /// <summary>
    /// An OpenAI-compatible speech server — OpenAI itself, DashScope, or something local. Empty
    /// means "use whatever runs on this machine", which on desktop is local Whisper.
    /// </summary>
    public string Endpoint { get; init; } = "";

    /// <summary>
    /// Which engine transcribes: <c>local</c> or <c>remote</c>. Local is the desktop default
    /// (PLAN §6a) because it is private and needs no endpoint at all; an endpoint that is also
    /// configured is then used only for reading the room aloud, for which there is no local
    /// option. <c>remote</c> sends audio to the endpoint instead.
    /// </summary>
    public string Engine { get; init; } = "local";

    public string TranscriptionModel { get; init; } = "whisper-1";

    public string SpeechModel { get; init; } = "tts-1";

    /// <summary>BCP-47 hint. Worth setting: detection on a short utterance is a coin flip.</summary>
    public string Language { get; init; } = "";

    /// <summary>
    /// Words the engine has never seen and will otherwise replace with something plausible.
    /// Room nicknames and agent names belong here.
    /// </summary>
    public string Vocabulary { get; init; } = "";

    /// <summary>
    /// Whether a transcript waits in the composer instead of sending itself. Off suits
    /// push-to-talk, which is deliberate; on suits leaving a microphone open.
    /// </summary>
    public bool ReviewBeforeSend { get; init; }

    /// <summary>Whose messages are read aloud: <c>off</c>, <c>agents</c>, or <c>everyone</c>.</summary>
    public string Readback { get; init; } = "agents";

    /// <summary>
    /// Leave the microphone open and let the gate cut utterances, rather than waiting to be
    /// pressed. Off by default: an open microphone is a decision, not a default.
    /// </summary>
    public bool AlwaysListening { get; init; }

    /// <summary>
    /// A global push-to-talk chord such as <c>Ctrl+Shift+Space</c>, held while speaking. Empty
    /// leaves the desktop with no global hotkey; the on-screen button still works.
    ///
    /// <para>This is the flow PLAN §6a is built around — press from any application, speak, and
    /// have it land in the room where the agents are, without switching windows.</para>
    /// </summary>
    public string Hotkey { get; init; } = "";

    /// <summary>
    /// Where the hotkey sends what it hears. Empty means the first room joined. It is deliberately
    /// not "whichever room is on screen": the point of a global hotkey is that the app is not on
    /// screen, so the destination has to be a decision made in advance rather than one the user is
    /// looking at.
    /// </summary>
    public string HomeRoom { get; init; } = "";
}
