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
