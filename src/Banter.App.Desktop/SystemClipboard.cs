using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Banter.App;
using SkiaSharp;

namespace Banter.App.Desktop;

/// <summary>
/// The system clipboard for desktop.
///
/// <para>Written by hand rather than pulled from a UI framework: the app targets plain
/// <c>net10.0</c> so it can be built and tested anywhere, and taking a dependency on WinForms or
/// WPF for two clipboard calls would tie the whole head to Windows.</para>
/// </summary>
public sealed class SystemClipboard : IClipboard
{
    public void SetText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsClipboard.SetText(text);
            }
            else
            {
                // pbcopy on macOS, xclip/wl-copy on Linux. Absent tooling is not an error worth
                // interrupting a chat for.
                PipeToTool(text);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"clipboard: could not copy text: {ex.Message}");
        }
    }

    public bool TrySetImage(string filePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath))
        {
            // Elsewhere the caller falls back to copying the path, which beats doing nothing.
            return false;
        }

        try
        {
            return WindowsClipboard.SetImage(filePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"clipboard: could not copy image: {ex.Message}");
            return false;
        }
    }

    private static void PipeToTool(string text)
    {
        foreach (var (tool, args) in new[] { ("pbcopy", ""), ("wl-copy", ""), ("xclip", "-selection clipboard") })
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = args,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                if (process is null)
                {
                    continue;
                }

                process.StandardInput.Write(text);
                process.StandardInput.Close();
                process.WaitForExit(2000);
                return;
            }
            catch (Exception)
            {
                // Try the next tool.
            }
        }
    }
}

/// <summary>
/// Win32 clipboard interop. Images go on as a DIB, which is what other applications expect from
/// "copy image" — a file path in <c>CF_HDROP</c> would paste as a link rather than a picture.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsClipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalFree(nint hMem);

    public static void SetText(string text)
    {
        var bytes = (text.Length + 1) * 2;   // UTF-16 plus its terminator
        var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
        if (handle == 0)
        {
            return;
        }

        var placed = false;
        try
        {
            var target = GlobalLock(handle);
            if (target == 0)
            {
                return;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            placed = Place(CF_UNICODETEXT, handle);
        }
        finally
        {
            // The clipboard owns the handle once SetClipboardData succeeds; freeing it then
            // would hand other applications a dangling pointer.
            if (!placed)
            {
                GlobalFree(handle);
            }
        }
    }

    public static bool SetImage(string filePath)
    {
        using var bitmap = SKBitmap.Decode(filePath);
        if (bitmap is null)
        {
            return false;
        }

        var dib = DibImage.ToDib(bitmap);
        var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)dib.Length);
        if (handle == 0)
        {
            return false;
        }

        var placed = false;
        try
        {
            var target = GlobalLock(handle);
            if (target == 0)
            {
                return false;
            }

            try
            {
                Marshal.Copy(dib, 0, target, dib.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            placed = Place(CF_DIB, handle);
            return placed;
        }
        finally
        {
            if (!placed)
            {
                GlobalFree(handle);
            }
        }
    }

    private static bool Place(uint format, nint handle)
    {
        // Another application can hold the clipboard open; a couple of retries covers the usual
        // moment of contention without blocking the UI thread for long.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    EmptyClipboard();
                    return SetClipboardData(format, handle) != 0;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(20);
        }

        return false;
    }

}

/// <summary>
/// Converting a bitmap to the <c>CF_DIB</c> byte layout. Pure, and deliberately outside the
/// Windows-only interop class: nothing about laying out a header and pixels needs Win32, and
/// keeping it separate is what lets it be tested anywhere.
/// </summary>
public static class DibImage
{
    /// <summary>
    /// Bottom-up 32-bit BGRA DIB: a BITMAPINFOHEADER followed by the pixels, which is the shape
    /// <c>CF_DIB</c> means. Rows run bottom to top because that is what a positive biHeight says.
    /// </summary>
    public static byte[] ToDib(SKBitmap source)
    {
        using var bgra = source.ColorType == SKColorType.Bgra8888
            ? source.Copy()
            : source.Copy(SKColorType.Bgra8888);

        var width = bgra.Width;
        var height = bgra.Height;
        var stride = width * 4;
        const int headerSize = 40;

        var dib = new byte[headerSize + (stride * height)];
        var header = BitConverter.GetBytes(headerSize);
        Buffer.BlockCopy(header, 0, dib, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(width), 0, dib, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(height), 0, dib, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((short)1), 0, dib, 12, 2);    // planes
        Buffer.BlockCopy(BitConverter.GetBytes((short)32), 0, dib, 14, 2);   // bits per pixel
        Buffer.BlockCopy(BitConverter.GetBytes(0), 0, dib, 16, 4);           // BI_RGB
        Buffer.BlockCopy(BitConverter.GetBytes(stride * height), 0, dib, 20, 4);

        var pixels = bgra.Bytes;
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, y * stride, dib, headerSize + ((height - 1 - y) * stride), stride);
        }

        return dib;
    }
}
