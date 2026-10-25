using System.Diagnostics;
using System.Runtime.InteropServices;
using Banter.App;

namespace Banter.App.Desktop;

/// <summary>
/// The native "choose a file" dialog: the common dialog on Windows, and whichever of zenity or
/// kdialog is installed on Linux.
///
/// <para>Always off the calling thread. The dialog runs its own message loop for as long as the
/// user is browsing, and the caller here is the render thread — blocking it would freeze the
/// window behind the dialog for the whole time it is open.</para>
/// </summary>
public sealed class SystemFilePicker : IFilePicker
{
    private const int MaxPathChars = 4096;

    // OFN_NOCHANGEDIR matters: without it the dialog leaves the process's working directory
    // wherever the user last browsed, and every later relative path resolves somewhere else.
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnExplorer = 0x00080000;

    private const int OfnOverwritePrompt = 0x00000002;

    public bool IsSupported => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && LinuxTool() is not null);

    public bool CanSave => IsSupported;

    public Task<string?> PickAsync(string title, CancellationToken cancellationToken = default) =>
        Task.Run(
            () => OperatingSystem.IsWindows() ? PickWindows(title) : PickLinux(title, cancellationToken),
            cancellationToken);

    public async Task<string?> SaveAsync(string name, string mimeType, byte[] content, string title, CancellationToken cancellationToken = default)
    {
        var path = await Task.Run(
            () => OperatingSystem.IsWindows() ? SaveWindows(title, name) : SaveLinux(title, name),
            cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return null;
        }

        await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string? PickWindows(string title) => Dialog(title, initial: "", save: false);

    private static string? SaveWindows(string title, string name) => Dialog(title, initial: name, save: true);

    /// <summary>The common dialog, opening or saving: the same struct, a different entry point,
    /// and for a save the suggested name in the buffer and a prompt before overwriting.</summary>
    private static string? Dialog(string title, string initial, bool save)
    {
        var buffer = Marshal.AllocHGlobal(MaxPathChars * sizeof(char));
        try
        {
            // The buffer doubles as the initial value, so it has to hold the suggested name - or
            // nothing - rather than whatever was in that memory.
            var seed = initial.Length >= MaxPathChars ? "" : initial;
            for (var i = 0; i < seed.Length; i++)
            {
                Marshal.WriteInt16(buffer, i * sizeof(char), seed[i]);
            }

            Marshal.WriteInt16(buffer, seed.Length * sizeof(char), 0);

            var ofn = new OpenFileName
            {
                LStructSize = Marshal.SizeOf<OpenFileName>(),
                // Double-null terminated, per the Win32 contract for this field.
                LpstrFilter = "All files\0*.*\0\0",
                NFilterIndex = 1,
                LpstrFile = buffer,
                NMaxFile = MaxPathChars,
                LpstrTitle = title,
                Flags = save
                    ? OfnOverwritePrompt | OfnPathMustExist | OfnNoChangeDir | OfnExplorer
                    : OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnExplorer,
            };

            var chosen = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
            if (!chosen)
            {
                return null;                                // cancelled, which is not a failure
            }

            var path = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The first of the usual dialog helpers that is actually installed.</summary>
    private static string? LinuxTool() =>
        new[] { "zenity", "kdialog" }.FirstOrDefault(tool =>
        {
            try
            {
                using var which = Process.Start(new ProcessStartInfo("which", tool)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });

                which?.WaitForExit(2000);
                return which?.ExitCode == 0;
            }
            catch (Exception)
            {
                return false;
            }
        });

    private static string? PickLinux(string title, CancellationToken cancellationToken) =>
        LinuxDialog(title, save: null);

    private static string? SaveLinux(string title, string name) => LinuxDialog(title, save: name);

    /// <summary>zenity or kdialog, opening or - with a suggested name - saving. Both confirm an
    /// overwrite themselves.</summary>
    private static string? LinuxDialog(string title, string? save)
    {
        if (LinuxTool() is not { } tool)
        {
            return null;
        }

        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        string[] arguments = (tool, save) switch
        {
            ("zenity", null) => ["--file-selection", $"--title={title}"],
            ("zenity", _) => ["--file-selection", "--save", $"--title={title}", $"--filename={save}"],
            (_, null) => ["--getopenfilename"],
            _ => ["--getsavefilename", save, "", "--title", title],
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // Both tools exit non-zero when the user cancels.
            return process.ExitCode == 0 && output.Trim() is { Length: > 0 } path ? path : null;
        }
        catch (Exception)
        {
            // A dialog helper that vanished between the check and the call. Not worth an
            // exception: the slash command still works.
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int LStructSize;
        public IntPtr HwndOwner;
        public IntPtr HInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpstrFilter;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpstrCustomFilter;
        public int NMaxCustFilter;
        public int NFilterIndex;
        public IntPtr LpstrFile;
        public int NMaxFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpstrFileTitle;
        public int NMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpstrTitle;
        public int Flags;
        public short NFileOffset;
        public short NFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpstrDefExt;
        public IntPtr LCustData;
        public IntPtr LpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpTemplateName;
        public IntPtr PvReserved;
        public int DwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OpenFileName ofn);
}
