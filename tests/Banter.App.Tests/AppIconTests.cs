using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The running app's icon, which the desktop window, the browser tab and the phone's recents card
/// all read off the CupriApp.
///
/// <para>Worth a test because the failure is silent. The icon is an embedded resource looked up by
/// a string, and a logical name that does not match returns null rather than throwing - so a typo
/// in the csproj or a moved file shows up as an app with no icon, on three platforms, noticed by
/// somebody else weeks later.</para>
/// </summary>
public sealed class AppIconTests
{
    [Fact]
    public void TheAppHasAnIcon()
    {
        var app = new BanterChatApp(new ChatViewModel());

        Assert.NotNull(app.Icon);
        Assert.NotEmpty(app.Icon);
    }

    /// <summary>Sniffed the way the host sniffs it: CupriApp picks the media type for the favicon's
    /// data URI off these bytes, so a file that is not a PNG would be announced as a JPEG.</summary>
    [Fact]
    public void TheIconIsAPng()
    {
        var app = new BanterChatApp(new ChatViewModel());
        var bytes = app.Icon!;

        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    /// <summary>The base class warns that reading it may hit a resource stream, and a window
    /// manager may ask on every frame.</summary>
    [Fact]
    public void ReadingItTwiceCostsOneRead()
    {
        var app = new BanterChatApp(new ChatViewModel());

        Assert.Same(app.Icon, app.Icon);
    }

    [Fact]
    public void ItReachesTheWebHostAsADataUri()
    {
        var app = new BanterChatApp(new ChatViewModel());

        Assert.StartsWith("data:image/png;base64,", app.IconDataUri);
    }
}
