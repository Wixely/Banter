using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Banter.App.Web.Tests;

/// <summary>
/// A browser, a mesh, and a message that arrives.
///
/// <para>Everything above the transport is covered headlessly elsewhere. What only this can reach
/// is the browser half of the connection: ICE negotiating over loopback, the DataChannel opening,
/// the conduit carrying HELLO and AUTH, and MessagePack decoding on a runtime that is not the one
/// the rest of the suite runs on.</para>
///
/// <para><b>Driving a canvas.</b> The UI is painted, not built, so there is nothing to click in
/// the usual sense. The host puts a transparent DOM mirror of the engine's semantics tree over the
/// canvas for screen readers, and that mirror is the handle: it is generated FROM the engine's
/// document every frame, so reading it is reading engine state - a sound oracle. Writing to it is
/// writing to a reflection, which is why input does not go through it. A real pointer passes
/// through the overlay (it is <c>pointer-events:none</c>) and lands on the canvas, where the
/// engine's own hit-testing takes it; keystrokes go to the host's hidden keyboard sink. In other
/// words: aim with the mirror, act on the canvas.</para>
/// </summary>
[Collection(nameof(MeshHead))]
public sealed class MeshChatTests(MeshHead mesh, ITestOutputHelper output)
{
    /// <summary>Booting wasm, then negotiating ICE and a DataChannel. Generous because it exists
    /// to fail a hang, not to measure anything.</summary>
    private const int BootMs = 90_000;
    private const int ConnectMs = 90_000;

    /// <summary>The host's hidden keyboard sink. Typing anywhere else either lands in the mirror -
    /// which is a reflection and reaches nothing - or steals focus from this, which is worse.</summary>
    private const string Keyboard = "#cupri-kbd";

    [Fact]
    public async Task ABrowserSignsInOverTheMeshAndTheServerKeepsWhatItSaid()
    {
        var page = await mesh.OpenAsync();
        page.Console += (_, m) => output.WriteLine($"[page] {m.Type}: {m.Text}");

        await page.GotoAsync(mesh.ClientUrl);
        await page.GetByRole(AriaRole.Button, new() { Name = "Connect" })
            .WaitForAsync(new() { Timeout = BootMs });

        // The node offered its own link, so nobody had to paste 400 characters. This is the
        // endpoint that removes the signalling server: the page is holding the node's remote
        // description before it opens a socket.
        var server = page.Locator("#cupri-a11y input[type=text]").First;
        await Assertions.Expect(server).ToHaveValueAsync(
            new System.Text.RegularExpressions.Regex("^cuprinet://intone/"), new() { Timeout = 30_000 });

        await TapAsync(page, AriaRole.Textbox, "your nick");
        await page.Locator(Keyboard).PressSequentiallyAsync(MeshHead.AdminUser);
        await TapAsync(page, AriaRole.Textbox, "Password");
        await page.Locator(Keyboard).PressSequentiallyAsync(MeshHead.AdminPassword);
        await TapAsync(page, AriaRole.Button, "Connect");

        // The composer replacing the sign-in form is the whole transport proving itself: ICE
        // agreed a pair, the channel opened, and the server answered an AUTH it could read.
        var composer = page.GetByRole(AriaRole.Textbox, new() { Name = "Message" });
        await composer.WaitForAsync(new() { Timeout = ConnectMs });
        output.WriteLine("connected over WebRTC");

        var said = $"hello from a headless browser {Guid.NewGuid():N}"[..44];
        await TapAsync(page, AriaRole.Textbox, "Message");
        await page.Locator(Keyboard).PressSequentiallyAsync(said);
        await page.Locator(Keyboard).PressAsync("Enter");

        // The SERVER's own account, not the sender's timeline. A client that rendered what it sent
        // without it ever arriving would satisfy the timeline and nothing else.
        var arrived = await WaitForMessageAsync(said, TimeSpan.FromSeconds(30));
        output.WriteLine($"server database says: {arrived ?? "(nothing)"}");
        Assert.NotNull(arrived);
    }

    /// <summary>
    /// A reload does not cost you your nick.
    ///
    /// <para>The desktop head writes a settings file and has always done this; the browser head
    /// persisted nothing at all, so every visit began with a blank sign-in screen (PLAN §2a had
    /// the web column at not-started for persisted settings). The password is deliberately not
    /// part of it, and neither is the server link - a link has a lifetime, and one remembered past
    /// its node's is a handshake that hangs.</para>
    ///
    /// <para>Reloaded rather than reopened, because localStorage belongs to the browser context:
    /// a new context is a new browser as far as this is concerned, which would prove nothing.</para>
    /// </summary>
    [Fact]
    public async Task ItRemembersWhoYouWereAfterAReload()
    {
        var page = await mesh.OpenAsync();
        await page.GotoAsync(mesh.ClientUrl);
        await page.GetByRole(AriaRole.Button, new() { Name = "Connect" })
            .WaitForAsync(new() { Timeout = BootMs });

        await TapAsync(page, AriaRole.Textbox, "your nick");
        await page.Locator(Keyboard).PressSequentiallyAsync(MeshHead.AdminUser);
        await TapAsync(page, AriaRole.Textbox, "Password");
        await page.Locator(Keyboard).PressSequentiallyAsync(MeshHead.AdminPassword);
        await TapAsync(page, AriaRole.Button, "Connect");

        // Only a real connection writes it - a typo should not be what you are offered back.
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Message" })
            .WaitForAsync(new() { Timeout = ConnectMs });

        await page.ReloadAsync();
        var nick = page.GetByRole(AriaRole.Textbox, new() { Name = "your nick" });
        await nick.WaitForAsync(new() { Timeout = BootMs });

        await Assertions.Expect(nick).ToHaveValueAsync(MeshHead.AdminUser, new() { Timeout = 30_000 });
        output.WriteLine($"sign-in came back holding '{MeshHead.AdminUser}'");

        // And the one thing that must NOT come back.
        var password = page.GetByRole(AriaRole.Textbox, new() { Name = "Password" });
        await Assertions.Expect(password).ToHaveValueAsync("", new() { Timeout = 5_000 });

        // Now prove it came from OUR store and not from the browser's. The mirror's fields are
        // real <input> elements over the painted ones - that is what makes the browser's editor,
        // IME and password manager work on them - so form autofill could repopulate that box and
        // this test would pass having proven nothing. Clear the store, reload, and it must be
        // blank again.
        await page.EvaluateAsync("() => window.localStorage.clear()");
        await page.ReloadAsync();
        var blank = page.GetByRole(AriaRole.Textbox, new() { Name = "your nick" });
        await blank.WaitForAsync(new() { Timeout = BootMs });
        await Assertions.Expect(blank).ToHaveValueAsync("", new() { Timeout = 30_000 });
        output.WriteLine("and blank again once the store was cleared, so it was not autofill");
    }

    /// <summary>
    /// Aims with the mirror and acts on the canvas: the mirror node reports where the engine
    /// painted the control, and the click is a real pointer at that point, which passes through
    /// the overlay and reaches the engine's hit test.
    /// </summary>
    private static async Task TapAsync(IPage page, AriaRole role, string name)
    {
        var target = page.GetByRole(role, new() { Name = name });
        await target.WaitForAsync();
        var box = await target.BoundingBoxAsync()
                  ?? throw new InvalidOperationException($"'{name}' has no box to aim at");
        await page.Mouse.ClickAsync(box.X + (box.Width / 2), box.Y + (box.Height / 2));
    }

    /// <summary>Polls the node's SQLite rather than sleeping a guess: the send is a round trip
    /// over a data channel and the write is the far end of it.</summary>
    private async Task<string?> WaitForMessageAsync(string text, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            // Read-only, and a copy of the path: the server owns this file and is writing to it.
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = mesh.DatabasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());

            try
            {
                await connection.OpenAsync();
                await using var query = connection.CreateCommand();
                query.CommandText = "SELECT room, sender, text FROM messages WHERE text = $text LIMIT 1";
                query.Parameters.AddWithValue("$text", text);
                await using var row = await query.ExecuteReaderAsync();
                if (await row.ReadAsync())
                {
                    return $"({row.GetString(0)}, {row.GetString(1)}, {row.GetString(2)})";
                }
            }
            catch (SqliteException)
            {
                // The file exists before the table does, and is locked while it is written.
            }

            await Task.Delay(250);
        }

        return null;
    }
}
