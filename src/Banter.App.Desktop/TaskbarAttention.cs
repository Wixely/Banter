using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Banter.App.Desktop;

/// <summary>
/// Asks the window manager for the user's attention — a flashing taskbar button on Windows.
///
/// <para>Nothing here raises or focuses the window. A chat client that steals focus because
/// somebody typed your name is a chat client people close: the taskbar asks, and the person
/// decides when to look.</para>
///
/// <para>Windows only for now. CupriFace's desktop host owns its window and exposes no attention
/// call, so the handle comes from the process rather than from the toolkit — which also means
/// this works without a CupriFace change. Every other platform is a no-op rather than an
/// exception: not being able to flash a taskbar is not a reason for a message to fail to
/// arrive.</para>
/// </summary>
public static class TaskbarAttention
{
    /// <summary>
    /// Flashes the taskbar button, unless this window is already the one in front.
    ///
    /// <para>Returns false when nothing was asked for — no window yet, already focused, or a
    /// platform with no way to ask — so a caller can tell "declined" from "done" while testing.</para>
    /// </summary>
    public static bool Raise()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return RaiseOnWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool RaiseOnWindows()
    {
        var handle = MainWindow();
        if (handle == IntPtr.Zero || handle == GetForegroundWindow())
        {
            // Already looking at it. Flashing then is the kind of notification people disable
            // the whole feature to be rid of.
            return false;
        }

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = handle,
            // TRAY|TIMERNOFG: flash the taskbar button, and keep flashing until the window comes
            // to the front. A fixed count would stop while somebody was still away from the desk,
            // which is exactly when they needed it.
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = 0,
            dwTimeout = 0,
        };

        return FlashWindowEx(ref info);
    }

    /// <summary>
    /// This process's main window. Re-read each time rather than cached: it is zero until the
    /// window exists, and a mention can arrive during startup.
    /// </summary>
    private static IntPtr MainWindow()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.Refresh();
            return self.MainWindowHandle;
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero;
        }
    }

    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    // DllImport rather than LibraryImport: the generated form needs a partial type and
    // AllowUnsafeBlocks, and turning unsafe code on for the whole head to flash a taskbar button
    // is a poor trade for two calls that are made once per mention.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    [DllImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr GetForegroundWindow();
}
