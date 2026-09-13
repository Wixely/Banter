using Banter.App;
using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Nothing runs off the side of a phone.
///
/// <para>Horizontal overflow is the failure mode of a desktop layout on a small screen, and it is
/// the one that leaves no trace on screen: a column past the right edge is not clipped, not
/// scrolled to and not drawn — it is simply not there, and the part of the interface it held is
/// unreachable with nothing to say so. <c>CF0072</c> (CupriFace 0.24.0) is the check that sees it.
/// </para>
/// </summary>
public sealed class PhoneFitTests(ITestOutputHelper output)
{
    /// <summary>
    /// The two ways a box can be on screen and hold nothing you can see. <c>CF0072</c> is the one
    /// that runs off the side; <c>CF0071</c> is the one that was actually happening here — a box
    /// squeezed to no width at all, which is what a flex row does to its children rather than
    /// letting them overflow.
    /// </summary>
    private static readonly string[] Invisible = ["CF0071", "CF0072"];

    [Theory]
    [MemberData(nameof(AppPages.OnSmall), MemberType = typeof(AppPages))]
    public void NothingIsSqueezedOutOfExistence(string page, int w, int h)
    {
        var app = AppPages.Showing(page);
        var report = CupriDoctor.Check(app.Html, app.Css, width: w, height: h, model: app.Model);

        var lost = report.Findings.Where(f => Invisible.Contains(f.Code)).ToList();
        foreach (var f in lost)
        {
            output.WriteLine(f.ToString());
        }

        // This is what it said before the narrow-window rules existed, at every one of these sizes:
        //   CF0071: <div class='main'> laid out 0x915 but has content inside it, so none of it is
        //   visible.
        // The chat pane. Not clipped and not scrolled past — zero pixels wide, because `.app` is a
        // flex row and the rail, sidebar and roster shrank in proportion until there was nothing
        // left to give it.
        Assert.True(lost.Count == 0, $"{lost.Count} thing(s) invisible on '{page}' at {w}x{h}");
    }

    [Theory]
    [MemberData(nameof(AppPages.Each), MemberType = typeof(AppPages))]
    public void TheDesignSizeStaysClean(string page)
    {
        // The size the four-column layout was drawn for. Here as a control: a phone finding is
        // only interesting if this one is quiet, otherwise the layout is broken everywhere and the
        // width is not what is wrong with it.
        var app = AppPages.Showing(page);
        var report = CupriDoctor.Check(
            app.Html, app.Css,
            width: (int)BanterChatApp.DesignWidth, height: (int)BanterChatApp.DesignHeight,
            model: app.Model);

        var lost = report.Findings.Where(f => Invisible.Contains(f.Code)).ToList();
        foreach (var f in lost)
        {
            output.WriteLine(f.ToString());
        }

        Assert.Empty(lost);
    }
}
