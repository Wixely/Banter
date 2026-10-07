using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Every head wires the admin pages.
///
/// <para>Read from the source, because what went wrong cannot be seen from a running app: the
/// Android and web heads wired none of the fourteen admin hooks, so an admin signing in on either
/// got four rail buttons opening four pages that did nothing - and an admin page with no hook is
/// indistinguishable from one waiting on a slow server. There is no behavioural assertion that
/// would have caught it; there is one to make about the construction.</para>
///
/// <para>The fourteen are one property now (<see cref="AdminHooks"/>), which is what makes this
/// test a single check rather than fourteen. A head can still forget it - that is what this is
/// for - but it can no longer be part of the way down the list.</para>
/// </summary>
public sealed class HeadAdminWiringTests(ITestOutputHelper output)
{
    /// <summary>
    /// The repository, found from the binaries first and this file only as a spare. A CI build
    /// rewrites source paths to a deterministic /_/... that exists nowhere, so CallerFilePath
    /// cannot be the primary - see tests/Banter.Server.Tests/Repo.cs, where that cost a release.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        if (Above(AppContext.BaseDirectory) is { } fromBinaries)
        {
            return fromBinaries;
        }

        if (Path.GetDirectoryName(here) is { Length: > 0 } source && Above(source) is { } fromSource)
        {
            return fromSource;
        }

        throw new DirectoryNotFoundException(
            $"no Banter.slnx above the binaries ({AppContext.BaseDirectory}) or the source ({here})");
    }

    private static string? Above(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Banter.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>The object initializer of the first <c>new BanterChatApp(…) { … }</c>, braces balanced.</summary>
    private static string? Construction(string source)
    {
        var at = source.IndexOf("new BanterChatApp(", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var open = source.IndexOf('{', at);
        if (open < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        return null;
    }

    [Fact]
    public void EveryHeadWiresTheAdminPages()
    {
        var heads = Directory
            .GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (Path: path, Construction: Construction(File.ReadAllText(path))))
            .Where(f => f.Construction is not null)
            .ToList();

        // Three today: the desktop, the phone and the browser. Raise it deliberately when a fourth
        // arrives - the point of the number is that it notices.
        Assert.Equal(3, heads.Count);

        foreach (var (path, construction) in heads)
        {
            var head = Path.GetFileName(Path.GetDirectoryName(path));
            output.WriteLine($"{head}: {construction!.Length} chars of initializer");
            Assert.Contains("Admin = ", construction);

            // The room list is the same shape of mistake one size down: a head that does not wire
            // it shows a list that was current when the session joined and silently stops there.
            Assert.Contains("RoomsListAsync = ", construction);
        }
    }
}
