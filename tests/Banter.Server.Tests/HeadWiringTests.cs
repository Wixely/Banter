using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Server.Tests;

/// <summary>
/// Every head wires the same server.
///
/// <para>Read from the source rather than from a running process, because what went wrong cannot
/// be observed from one. The mesh head constructed its <c>BanterServer</c> without
/// <c>tools:</c> — so every agent on the mesh saw no tools whatever the deployment held — and
/// nothing anywhere reported it, since an agent holding no grants and an agent on a server with no
/// broker answer <c>tools/list</c> identically. There is no assertion to make about behaviour that
/// would have caught it; there is one to make about the call.</para>
///
/// <para>A heads-count assertion comes with it: a test that reads the source has to fail when the
/// thing it reads moves, not quietly pass over nothing.</para>
/// </summary>
public sealed class HeadWiringTests(ITestOutputHelper output)
{
    /// <summary>
    /// Walked up from THIS FILE, not from the test binaries. Where the binaries land is a build
    /// argument (a locked bin forced a run out to the temp directory once, and a finder rooted at
    /// AppContext.BaseDirectory then reported no repository at all); where this file sits is the
    /// repository by definition.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(here)!);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Banter.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"no Banter.slnx above {here}");
    }

    /// <summary>The arguments of the first <c>new BanterServer(…)</c>, parens balanced.</summary>
    private static string? Construction(string source)
    {
        var open = source.IndexOf("new BanterServer(", StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        var start = source.IndexOf('(', open);
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')' && --depth == 0) return source[start..(i + 1)];
        }

        return null;
    }

    [Fact]
    public void EveryHeadHandsTheServerItsTools()
    {
        var heads = Directory
            .GetFiles(Path.Combine(RepoRoot(), "src"), "Program.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .Select(f => (f.Path, Construction: Construction(f.Source)))
            .Where(f => f.Construction is not null)
            .ToList();

        // Two today: the socket server and the mesh server. Raise it deliberately when a third
        // arrives — the point of the number is that it notices.
        Assert.Equal(2, heads.Count);

        foreach (var (path, construction) in heads)
        {
            var head = Path.GetFileName(Path.GetDirectoryName(path));
            output.WriteLine($"{head}: {construction!.Length} chars of arguments");
            Assert.Contains("tools:", construction);
        }
    }
}
