using System.Text.Json;
using Banter.App;
using Xunit;

namespace Banter.App.Tests;

public sealed class BanterSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "banter-settings-" + Guid.NewGuid().ToString("N"));

    private string At(string name = "settings.json") => Path.Combine(_dir, name);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void RoundTripsThroughDisk()
    {
        var settings = new BanterSettings
        {
            Server = "tcp://localhost:7777",
            User = "alice",
            Rooms = ["#main", "#dev"],
            Scrollback = 1234,
        };

        Assert.True(settings.TrySave(At()));
        var loaded = BanterSettings.Load(At());

        Assert.Equal("tcp://localhost:7777", loaded.Server);
        Assert.Equal("alice", loaded.User);
        Assert.Equal(["#main", "#dev"], loaded.Rooms);
        Assert.Equal(1234, loaded.Scrollback);
    }

    [Fact]
    public void NoSecretIsEverWrittenToDisk()
    {
        var settings = new BanterSettings { Server = "tcp://h:1", User = "alice", Rooms = ["#main"] };
        settings.TrySave(At());

        var json = File.ReadAllText(At());

        // The type has no secret field; this asserts nobody adds one without noticing.
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(names, n => n.Contains("pass") || n.Contains("secret") || n.Contains("watchword") || n.Contains("token"));
    }

    [Fact]
    public void MissingFileYieldsUsableDefaultsRatherThanThrowing()
    {
        var loaded = BanterSettings.Load(At("does-not-exist.json"));

        Assert.False(loaded.IsComplete);
        Assert.Empty(loaded.Rooms);
        Assert.Equal(5_000, loaded.Scrollback);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaultsAndReportsWhy()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(At(), "{ this is not json");
        string? reported = null;

        var loaded = BanterSettings.Load(At(), p => reported = p);

        // Starting with defaults beats refusing to launch over a preferences file.
        Assert.False(loaded.IsComplete);
        Assert.NotNull(reported);
        Assert.Contains("settings.json", reported);
    }

    [Theory]
    [InlineData("", "alice", false)]
    [InlineData("tcp://h:1", "", false)]
    [InlineData("not a uri", "alice", false)]
    [InlineData("tcp://h:1", "alice", true)]
    public void CompletenessRequiresAServerUriAndAUser(string server, string user, bool complete) =>
        Assert.Equal(complete, new BanterSettings { Server = server, User = user }.IsComplete);

    [Fact]
    public void CommandLineValuesOverrideStoredOnesAndOmissionsKeepThem()
    {
        var stored = new BanterSettings { Server = "tcp://h:1", User = "alice", Rooms = ["#main"] };

        var overridden = stored.With(server: null, user: "bob", rooms: null);

        Assert.Equal("tcp://h:1", overridden.Server);   // kept
        Assert.Equal("bob", overridden.User);           // overridden
        Assert.Equal(["#main"], overridden.Rooms);      // kept
    }

    [Fact]
    public void AnEmptyRoomListDoesNotWipeStoredRooms()
    {
        var stored = new BanterSettings { Rooms = ["#main"] };

        Assert.Equal(["#main"], stored.With(null, null, []).Rooms);
        Assert.Equal(["#other"], stored.With(null, null, ["#other"]).Rooms);
    }
}
