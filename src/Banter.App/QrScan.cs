using ZXing;
using ZXing.Common;

namespace Banter.App;

/// <summary>
/// Finding a server link in a camera frame.
///
/// <para>Deliberately knows nothing about cameras: it takes the luminance plane every platform
/// already has — Android's <c>YUV_420_888</c> Y plane, a browser's greyscaled canvas — so the
/// part that can be tested is separate from the part that needs a lens pointed at something.</para>
/// </summary>
public static class QrScan
{
    /// <summary>
    /// Shared and reused. Constructing a reader per frame allocates its whole decoding apparatus
    /// thirty times a second for no benefit; nothing here keeps per-frame state.
    /// </summary>
    private static readonly BarcodeReaderGeneric Reader = new()
    {
        Options = new DecodingOptions
        {
            // A QR and nothing else. The default hunts every format ZXing knows on every frame,
            // which is most of the work for none of the answers — nothing in Banter is a barcode.
            PossibleFormats = [BarcodeFormat.QR_CODE],

            // A link fills the symbol, so the code is dense and the camera is usually reading it
            // off a screen at an angle. Worth the extra passes: the alternative to a slower frame
            // is a frame that finds nothing and a person holding a phone still for longer.
            TryHarder = true,
        },
    };

    /// <summary>
    /// The text of a QR code in this frame, or null when there is not one in it — which is the
    /// ordinary case, most frames, and not a failure worth reporting anywhere.
    /// </summary>
    /// <param name="luminance">One byte per pixel, row by row. Android's Y plane as it comes.</param>
    public static string? Read(ReadOnlySpan<byte> luminance, int width, int height)
    {
        if (width <= 0 || height <= 0 || luminance.Length < width * height)
        {
            return null;
        }

        // ZXing takes an array, and a camera frame arrives as one; copying only when the span is
        // not already the whole buffer keeps the common path allocation-free.
        var pixels = luminance.Length == width * height
            ? luminance.ToArray()
            : luminance[..(width * height)].ToArray();

        // The last argument is reverseHorizontal: a frame is read as it arrives, because a front
        // camera's mirroring is the preview's business and not the decoder's.
        var source = new PlanarYUVLuminanceSource(
            pixels, width, height, 0, 0, width, height, false);

        return Reader.Decode(source)?.Text;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is something the connect screen could actually use, so a
    /// scanner can keep looking rather than stopping on the first code it sees.
    ///
    /// <para>A phone is pointed at the world, and the world is full of QR codes — a parcel label,
    /// a poster, a Wi-Fi code on a café wall. Stopping on one of those and filling the Server
    /// field with it would be worse than not scanning at all, because it looks like it worked.</para>
    /// </summary>
    public static bool LooksLikeAServer(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme.ToLowerInvariant() is "tcp" or "cuprinet" or "cupri";
}
