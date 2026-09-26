using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace Banter.App.Web.Tests;

/// <summary>
/// A real <c>banter-nodestar</c>, serving the published web head, with a real browser pointed at
/// it.
///
/// <para>This is the one arrangement that exercises what nothing else can: ICE, the browser's own
/// DataChannel, the conduit, and every Banter verb riding it. PLAN §2.5 listed all three as having
/// no suite. It is also the only shape that can catch a MessagePack formatter the trimmer removed,
/// because that failure boots, paints, 404s nothing, and dies on the first frame it decodes.</para>
///
/// <para>One process serves both halves, which is the point: the node that carries the mesh hands
/// out the page that dials it.</para>
/// </summary>
public sealed class MeshHead : IAsyncLifetime
{
    private const string PublishedTo = "src/Banter.App.Web/bin/Release/net10.0/browser-wasm/publish";
    private const string NodestarUnder = "src/Banter.Server.Nodestar/bin";

    /// <summary>What the launch configuration uses, so a person can poke at the same node by hand.</summary>
    public const string AdminUser = "admin";
    public const string AdminPassword = "banter";

    private Process? _node;
    private string _dataDirectory = "";
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly List<string> _output = [];

    /// <summary>Where the node serves the client, mount point included.</summary>
    public string ClientUrl { get; private set; } = "";

    /// <summary>The node's SQLite file — the server's own account of what it received, which is a
    /// stronger oracle than the timeline the sender is looking at.</summary>
    public string DatabasePath => Path.Combine(_dataDirectory, "banter.db");

    public async Task InitializeAsync()
    {
        var root = RepositoryRoot();
        var client = Path.Combine(root, PublishedTo);
        if (!Directory.Exists(client))
        {
            throw new DirectoryNotFoundException(
                $"The web head has not been published to '{client}'. Run:\n" +
                "  dotnet publish src/Banter.App.Web/Banter.App.Web.csproj --configuration Release");
        }

        var nodestar = FindNodestar(root);
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"banter-mesh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);

        // Every port off the defaults. A developer running the launch configuration has a node on
        // 7771-7774 already, and a test that fights it for a port fails in a way that looks like
        // the mesh being broken rather than like two nodes.
        var (site, listen, webRtc, web) = (FreePort(), FreePort(), FreePort(), FreePort());
        ClientUrl = $"http://localhost:{web}/_nodestar/app";

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     nodestar,
                     "--data", _dataDirectory,
                     "--network", $"banter-test-{Guid.NewGuid():N}"[..24],
                     "--no-qr",
                     "--client", client,
                     "--site-port", site.ToString(),
                     "--listen-port", listen.ToString(),
                     "--webrtc-port", webRtc.ToString(),
                     "--web-port", web.ToString(),
                 })
        {
            start.ArgumentList.Add(argument);
        }

        // The account is seeded on first run only, and this data directory is new every time, so
        // this is the password - not "whatever it was the first time somebody ran it".
        start.Environment["BANTER_ADMIN_PASSWORD"] = AdminPassword;

        _node = Process.Start(start) ?? throw new InvalidOperationException("could not start banter-nodestar");
        var ready = new TaskCompletionSource();
        _node.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_output) _output.Add(e.Data);
            if (e.Data.Contains("Press Ctrl+C to stop", StringComparison.Ordinal)) ready.TrySetResult();
        };
        _node.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.Add(e.Data); };
        _node.BeginOutputReadLine();
        _node.BeginErrorReadLine();

        // A CupriNet node is not instant: it builds an overlay identity, binds four ports and
        // brings a site online before it can say anything.
        if (await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromMinutes(2))) != ready.Task)
        {
            throw new TimeoutException("banter-nodestar never reported ready:\n" + Log());
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();

        if (_node is { HasExited: false })
        {
            _node.Kill(entireProcessTree: true);
            await _node.WaitForExitAsync();
        }

        _node?.Dispose();

        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { /* the node's SQLite handles can outlive the kill by a moment */ }
    }

    /// <summary>Everything the node said, for a failure that needs it.</summary>
    public string Log() { lock (_output) return string.Join('\n', _output); }

    public async Task<IPage> OpenAsync()
    {
        var context = await _browser!.NewContextAsync();
        return await context.NewPageAsync();
    }

    private static string FindNodestar(string root)
    {
        var bin = Path.Combine(root, NodestarUnder);
        var built = Directory.Exists(bin)
            ? Directory.GetFiles(bin, "banter-nodestar.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        return built ?? throw new FileNotFoundException(
            "banter-nodestar has not been built. Run:\n" +
            "  dotnet build src/Banter.Server.Nodestar/Banter.Server.Nodestar.csproj");
    }

    /// <summary>A port the OS has just confirmed is free. Racy in principle and fine in practice:
    /// the alternative is a fixed number, which races every other thing on the machine.</summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Banter.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("no Banter.slnx above the test binaries");
    }
}

[CollectionDefinition(nameof(MeshHead))]
public sealed class MeshHeadCollection : ICollectionFixture<MeshHead>;
