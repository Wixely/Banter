using System.Runtime.CompilerServices;

namespace Banter.Server.Tests;

/// <summary>
/// Where the repository is, for the two tests that read files out of it rather than out of their
/// own output.
///
/// <para>Two ways of asking, in this order, because each one fails somewhere the other works.
/// Walking up from the BINARIES is the one that holds on CI, and it is the default. Walking up
/// from this SOURCE FILE is the fallback, for a run whose output was redirected out of the
/// repository - a locked bin, most likely - where there is no repository above the binaries at
/// all.</para>
///
/// <para>The fallback cannot be the primary, which is the mistake that brought this file into
/// being. <c>[CallerFilePath]</c> is baked in by the compiler, and a CI build sets
/// <c>ContinuousIntegrationBuild</c>, which rewrites source paths to a deterministic
/// <c>/_/…</c> - a path that exists nowhere. Both tests then failed on CI while passing on every
/// developer machine, which is the most expensive way for a path to be wrong.</para>
/// </summary>
internal static class Repo
{
    public static string Root([CallerFilePath] string here = "")
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

    /// <summary>The first directory at or above this one holding the solution, or null.</summary>
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
}
