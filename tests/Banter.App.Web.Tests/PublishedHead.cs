using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Banter.App.Web.Tests;

/// <summary>
/// The published web head, served and opened in a real browser.
///
/// <para>Shared across the whole class run because both halves are expensive and neither is
/// stateful: a browser launch is seconds, and every test wants the same directory of files.</para>
/// </summary>
public sealed class PublishedHead : IAsyncLifetime
{
    /// <summary>Where a Release publish leaves the head, relative to the repository root.</summary>
    private const string PublishedTo = "src/Banter.App.Web/bin/Release/net10.0/browser-wasm/publish";

    private WebApplication? _server;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>The address the head is served from, port included.</summary>
    public string BaseUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var root = FindPublishedHead();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _server = builder.Build();

        // A .wasm served as text/plain is refused by the browser's streaming compiler, and the
        // page then fails in a way that looks like the app is broken rather than the server.
        // .dat is the ICU data the globalization stack loads.
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".wasm"] = "application/wasm";
        types.Mappings[".dat"] = "application/octet-stream";
        types.Mappings[".blat"] = "application/octet-stream";

        _server.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = new PhysicalFileProvider(root),
        });
        _server.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(root),
            ContentTypeProvider = types,
            // Anything unmapped still goes out as bytes rather than a 404, which is what a static
            // host would do. A missing content type must not be the thing that fails the suite.
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",
        });

        await _server.StartAsync();
        BaseUrl = _server.Urls.First();

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();

        if (_server is not null)
        {
            await _server.StopAsync();
            await _server.DisposeAsync();
        }
    }

    /// <summary>A fresh page — its own context, so console output and network log belong to one
    /// test rather than to whichever ran first.</summary>
    public async Task<IPage> OpenAsync()
    {
        var context = await _browser!.NewContextAsync();
        return await context.NewPageAsync();
    }

    /// <summary>
    /// Finds the publish output, or says exactly what to run.
    ///
    /// <para>Deliberately a failure and not a skip. A suite that skips itself when its input is
    /// missing reports green on a machine where it tested nothing, and the one place that matters
    /// most is CI - which is exactly where nobody reads the skip count.</para>
    /// </summary>
    private static string FindPublishedHead()
    {
        if (Environment.GetEnvironmentVariable("BANTER_WEB_PUBLISH") is { Length: > 0 } configured)
        {
            return Directory.Exists(configured)
                ? configured
                : throw new DirectoryNotFoundException(
                    $"BANTER_WEB_PUBLISH points at '{configured}', which does not exist.");
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Banter.slnx")))
            {
                continue;
            }

            var published = Path.Combine(directory.FullName, PublishedTo);
            return Directory.Exists(published)
                ? published
                : throw new DirectoryNotFoundException(
                    $"The web head has not been published to '{published}'. Run:\n" +
                    "  dotnet publish src/Banter.App.Web/Banter.App.Web.csproj --configuration Release\n" +
                    "or point BANTER_WEB_PUBLISH at a wwwroot published elsewhere.");
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root (no Banter.slnx above the test binaries).");
    }
}

[CollectionDefinition(nameof(PublishedHead))]
public sealed class PublishedHeadCollection : ICollectionFixture<PublishedHead>;
