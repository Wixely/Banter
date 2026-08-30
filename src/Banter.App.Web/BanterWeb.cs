using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Banter.Client.Core;
using Banter.Transport.Shrine;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Vessel;

namespace Banter.App.Web;

/// <summary>
/// Everything the browser head does that the desktop head does not do the same way: dial over
/// WebRTC, and take a link a node left for it. The room itself, its timeline, its commands and its
/// verbs are all shared code.
/// </summary>
public static class BanterWeb
{
    /// <summary>Matches the desktop head's default; there is no settings file to read one from.</summary>
    private const int HistoryPageSize = 50;

    private static ChatViewModel _viewModel = null!;
    private static BanterChatSession? _session;

    public static BanterChatApp Build()
    {
        _viewModel = new ChatViewModel();

        // Every callback resolves the session when it is called rather than when it is wired: the
        // app exists from the first frame, and the session only after someone connects. The desktop
        // head can build them in the other order because it connects before it has a window.
        var app = new BanterChatApp(_viewModel)
        {
            ConnectAsync = ConnectAsync,
            SendAsync = (room, text) => _session?.SendAsync(room, text) ?? Task.CompletedTask,
            // Room switching is local: the backlog is held per room and history was filled at join.
            RoomSelected = _ => { },
            LoadOlderAsync = room => _session?.LoadOlderAsync(room, HistoryPageSize) ?? Task.CompletedTask,
            CommandAsync = (room, text) => _session?.CommandAsync(room, text) ?? Task.CompletedTask,
            DownloadAsync = id => _session?.DownloadAsync(id) ?? Task.CompletedTask,
            JoinRoomAsync = room => _session?.JoinAsync(room, HistoryPageSize) ?? Task.CompletedTask,
            ToolsOpenAsync = filter => _session?.LoadToolsAsync(filter) ?? Task.CompletedTask,
            ToolsSaveAsync = (agent, tools) => _session?.SaveToolsAsync(agent, tools) ?? Task.CompletedTask,
        };

        // No server is configured in a browser — there are no command-line arguments to read one
        // from — so the connect screen is where every session starts. The server is the node's
        // intonation link: pasted in, or seeded by a node that was asked to leave one.
        _viewModel.ShowConnect(server: "", user: "");
        _ = WatchForSeedAsync();

        return app;
    }

    /// <summary>
    /// Watches for a link a node left for us at <c>seed.json</c> (its <c>--seed-file</c>). Absent in
    /// a normal deployment, where the person pastes a link — so a miss is silence, not an error.
    ///
    /// <para>Polled rather than read once, and never awaited by the caller: under a "server +
    /// client" launch the two start together and the browser regularly wins the race, so a single
    /// read at boot finds nothing. Not blocking startup matters just as much — a deployment with no
    /// seed at all must not be made to wait for one that is never coming.</para>
    /// </summary>
    private static async Task WatchForSeedAsync()
    {
        // An absolute URL, because a browser HttpClient has no BaseAddress and a relative one
        // throws rather than resolving against the page. That failure looked exactly like "no seed
        // yet" and retried silently for thirty seconds, which is what a catch-all buys you.
        Uri seedUri;
        try
        {
            var baseUri = JSHost.GlobalThis.GetPropertyAsJSObject("document")?.GetPropertyAsString("baseURI");
            seedUri = new Uri(new Uri(baseUri ?? "/"), "seed.json");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[banter] cannot resolve the page address, so no seed: {ex.Message}");
            return;
        }

        using var http = new HttpClient();

        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                // JsonDocument rather than a deserialised record: reading one string needs no
                // reflection, and the trimmer can see through it.
                using var seed = JsonDocument.Parse(
                    await http.GetStringAsync(seedUri).ConfigureAwait(false));

                if (seed.RootElement.TryGetProperty("link", out var element) &&
                    element.GetString() is { Length: > 0 } link)
                {
                    // Offered, not imposed: declined once someone has typed their own, or once the
                    // screen has gone.
                    _viewModel.SuggestConnectServer(link);
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // No seed file. Expected in any deployment that does not use one, and expected for
                // the first few tries when the node is still starting.
            }
            catch (Exception ex)
            {
                // Anything else is a fault of ours, and silence would hide it the way it hid the
                // relative-URI bug above.
                Console.Error.WriteLine($"[banter] seed watch stopped: {ex.Message}");
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What the Connect button does. The whole of the web head's networking: a WebRTC DataChannel
    /// becomes a vessel, the vessel carries a Pilgrimage, the Pilgrimage carries a conduit, and
    /// every Banter verb above that is the same code the desktop runs.
    /// </summary>
    private static async Task ConnectAsync(string server, string user, string password)
    {
        try
        {
            var transport = new ShrineClientTransport(
                async (intonation, cancellationToken) =>
                {
                    var channel = await BrowserDataChannel.ConnectAsync(intonation, cancellationToken);
                    return new DataChannelVessel(channel);
                },
                new BouncyCastleSuite());

            var client = await BanterClient.ConnectAsync(transport, new Uri(server.Trim()), user, password);

            var session = new BanterChatSession(client, _viewModel);
            _session = session;
            _viewModel.Connected();

            // The status badge starts at "Disconnected" and only a head moves it. Without this the
            // room opens looking broken while working perfectly.
            _viewModel.Post(() =>
            {
                _viewModel.SetNick(client.Nick);
                _viewModel.SetStatus("Connected", connected: true);
            });

            try
            {
                await session.JoinAsync("#main", HistoryPageSize);
            }
            catch (Exception ex)
            {
                _viewModel.Post(() => _viewModel.System("#main", $"could not join #main: {ex.Message}"));
            }

            // Probe once, so the tools control appears only for an account the server would let
            // manage them.
            await session.LoadToolsAsync("");
        }
        catch (Exception ex)
        {
            // Shown on the connect card rather than logged: in a browser there is no console the
            // person is looking at, and a button that silently does nothing is the worst outcome.
            _viewModel.ConnectFailed(ex.Message);
        }
    }
}
