using Banter.App;
using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Repeated controls agree on their size, and no two rounded boxes meet with nothing between them.
///
/// <para><c>CF0073</c> and <c>CF0074</c> arrived in CupriFace 0.40.0 and 0.41.0, and both describe
/// defects this app has actually shipped. CF0073 is a control in a column or row that is a
/// different size from the others like it — the shape of the room name painting across the member
/// count beside it, fixed in 0.12.1. CF0074 is two boxes sitting flush where both are rounded, so
/// the curves collide instead of reading as separate things — which is the composer's nested-box
/// problem from 0.12.2 stated as a rule.</para>
///
/// <para>Neither is error-level, so <see cref="MarkupHealthTests"/> does not see them and
/// <see cref="PhoneFitTests"/> filters to the two codes about things being invisible. Both pages
/// were clean the day 0.41.0 was taken; this is what keeps them that way, because a warning nobody
/// reads is the same as a warning nobody raised.</para>
/// </summary>
public sealed class PeerAndSeamTests(ITestOutputHelper output)
{
    /// <summary>The desktop window as well as the phones: CF0073 is about peers disagreeing, and a
    /// column of controls can agree at one width and disagree at another.</summary>
    [Theory]
    [MemberData(nameof(AppPages.OnAnySize), MemberType = typeof(AppPages))]
    public void PeersAgreeAndCurvesDoNotCollide(string page, int w, int h)
    {
        var app = AppPages.Showing(page);
        var report = CupriDoctor.Check(app.Html, app.Css, width: w, height: h, model: app.Model);

        var found = report.Findings.Where(f => f.Code is "CF0073" or "CF0074").ToList();
        foreach (var f in found)
        {
            output.WriteLine(f.ToString());
        }

        Assert.True(found.Count == 0, $"{found.Count} finding(s) on '{page}' at {w}x{h}");
    }
}
