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

    /// <summary>
    /// Every CSS property the sheet declares is one the engine actually implements.
    ///
    /// <para><c>CF0050</c> is an ignored declaration, and an ignored declaration is the same class
    /// of failure as a misspelt binding above: the page renders, nothing complains, and the thing
    /// you wrote simply never happens. <c>caret-color: #fb7185</c> lived in the composer for a
    /// whole release doing nothing - CupriDoctor had been reporting it the entire time and no test
    /// read the code, so the only evidence was a caret that was never the colour it was set to,
    /// which is not something anybody notices.</para>
    ///
    /// <para>Checked at the design size only. An unsupported property is a fact about the
    /// stylesheet, not about the viewport, so the other sizes would report the same thing eight
    /// more times.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AppPages.Each), MemberType = typeof(AppPages))]
    public void NothingInTheSheetIsSilentlyIgnored(string page)
    {
        var app = AppPages.Showing(page);
        var report = CupriDoctor.Check(
            app.Html, app.Css,
            width: (int)BanterChatApp.DesignWidth, height: (int)BanterChatApp.DesignHeight,
            model: app.Model);

        var ignored = report.Findings.Where(f => f.Code == "CF0050").ToList();
        foreach (var f in ignored)
        {
            output.WriteLine(f.ToString());
        }

        Assert.True(ignored.Count == 0, $"{ignored.Count} ignored declaration(s) on '{page}'");
    }

    /// <summary>
    /// Secondary text is readable, not merely present.
    ///
    /// <para><c>CF0090</c> is text whose contrast against what is behind it falls below the AA
    /// ratio for its size. Seven distinct colour pairs failed it on this app for a long time, the
    /// worst at 2.5:1 - the "choose one of the things on the left" line that IS the content of an
    /// empty management page. The dark theme is what hides this: every one of them looks
    /// deliberate, and a grey that is 2.5:1 and a grey that is 4.6:1 are the same design decision
    /// to anybody who can already read them both.</para>
    ///
    /// <para>The failures collapsed into two values rather than seven, which is the useful part:
    /// hints and status at <c>#747f91</c>, the brighter tier - subtitles, section titles, the room
    /// hash, muted state - at <c>#808b9c</c>. Both clear 4.5:1 on every background in the sheet
    /// with the dimmer-to-brighter ladder intact, so the tiers still read as tiers.</para>
    ///
    /// <para>This sees what the furnished model renders at the design size, like the checks above
    /// it. Text that only appears in some other state - an away author's name, say - is outside
    /// what any of these can speak for.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AppPages.Each), MemberType = typeof(AppPages))]
    public void SecondaryTextIsReadable(string page)
    {
        var app = AppPages.Showing(page);
        var report = CupriDoctor.Check(
            app.Html, app.Css,
            width: (int)BanterChatApp.DesignWidth, height: (int)BanterChatApp.DesignHeight,
            model: app.Model);

        var dim = report.Findings.Where(f => f.Code == "CF0090").ToList();
        foreach (var f in dim)
        {
            output.WriteLine(f.ToString());
        }

        Assert.True(dim.Count == 0, $"{dim.Count} unreadable run(s) of text on '{page}'");
    }
}
