using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Every <c>{{binding}}</c> names something that exists, on every page.
///
/// <para>A misspelt binding is the quietest failure the engine has: an unknown property resolves to
/// null, null formats as the empty string, and the element renders perfectly with nothing in it. On
/// screen that is indistinguishable from a model with no data yet, so it survives a review and a
/// screenshot both — which is exactly why it wants a test rather than an eye.</para>
///
/// <para>This could not be asserted until CupriFace 0.24.1. Before it, a <c>data-repeat</c> nested
/// inside another resolved its collection against the root model only, so the ask panel's own rows
/// reported three CF0060 ERRORS against correct markup (CupriFace#160) and any real one would have
/// been lost among them.</para>
/// </summary>
public sealed class MarkupHealthTests(ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(AppPages.Each), MemberType = typeof(AppPages))]
    public void NothingOnThePageNamesSomethingThatIsNotThere(string page)
    {
        var app = AppPages.Showing(page);
        var report = CupriDoctor.Check(
            app.Html, app.Css,
            width: (int)BanterChatApp.DesignWidth, height: (int)BanterChatApp.DesignHeight,
            model: app.Model);

        var errors = report.Findings.Where(f => f.Severity == Severity.Error).ToList();
        foreach (var f in errors)
        {
            output.WriteLine(f.ToString());
        }

        Assert.True(errors.Count == 0, $"{errors.Count} error-level finding(s) on '{page}'");
    }
}
