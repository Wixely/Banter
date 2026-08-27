using Banter.App;
using Bantz.Input;

namespace Banter.App.Desktop;

/// <summary>
/// The global push-to-talk key (PLAN §6a) — held from any application, speaking into the home
/// room without switching windows. The flow the desktop client exists for.
///
/// <para>Hold, not toggle, and that is the whole reason this is separate from the on-screen
/// button: <see cref="IGlobalInputService"/> reports the press and the release, so the key can
/// mean "the microphone is open for exactly as long as I hold this". CupriFace gives clicks only,
/// which is why the button is a toggle instead.</para>
/// </summary>
public sealed class DesktopHotkey : IDisposable
{
    private readonly IGlobalInputService _input;
    private readonly IDisposable _registration;
    private readonly Func<bool, Task> _setOpen;

    private DesktopHotkey(IGlobalInputService input, IDisposable registration, Func<bool, Task> setOpen)
    {
        _input = input;
        _registration = registration;
        _setOpen = setOpen;

        _input.Pressed += OnPressed;
        _input.Released += OnReleased;
        _input.Enabled = true;
    }

    /// <summary>What the key is, for saying so in the timeline.</summary>
    public string Display { get; private init; } = "";

    /// <summary>
    /// Registers <paramref name="chordText"/>, or returns null having said why through
    /// <paramref name="warn"/>. A hotkey that silently is not the one asked for reads as a broken
    /// keyboard, and nothing in the app would ever say otherwise.
    /// </summary>
    public static DesktopHotkey? TryRegister(string chordText, Func<bool, Task> setOpen, Action<string> warn)
    {
        if (chordText.Length == 0)
        {
            return null;
        }

        if (!HotkeyChord.TryParse(chordText, out var chord, out var problem))
        {
            warn($"hotkey '{chordText}' ignored: {problem}.");
            return null;
        }

        IGlobalInputService input;
        try
        {
            input = GlobalInputService.Create();
        }
        catch (Exception ex)
        {
            warn($"global hotkeys are unavailable here: {ex.Message}");
            return null;
        }

        if (!input.IsSupported)
        {
            // Honest on platforms Bantz.Input does not cover. The on-screen button still works,
            // so this is a missing convenience rather than a missing feature.
            warn($"global hotkeys are not supported on this platform; use the Talk button.");
            (input as IDisposable)?.Dispose();
            return null;
        }

        var binding = new InputBinding
        {
            Id = "banter.ptt",
            Device = InputDevice.Keyboard,
            Code = chord.Code,
            Modifiers = Modifiers(chord),
            DisplayName = chord.Display,
        };

        try
        {
            var registration = input.Register(binding);
            return new DesktopHotkey(input, registration, setOpen) { Display = chord.Display };
        }
        catch (Exception ex)
        {
            // Most often another application already owns the chord.
            warn($"could not register {chord.Display}: {ex.Message}");
            (input as IDisposable)?.Dispose();
            return null;
        }
    }

    private static KeyboardModifiers Modifiers(KeyChord chord)
    {
        var modifiers = KeyboardModifiers.None;
        if (chord.Control)
        {
            modifiers |= KeyboardModifiers.Control;
        }

        if (chord.Shift)
        {
            modifiers |= KeyboardModifiers.Shift;
        }

        if (chord.Alt)
        {
            modifiers |= KeyboardModifiers.Alt;
        }

        if (chord.Windows)
        {
            modifiers |= KeyboardModifiers.Windows;
        }

        return modifiers;
    }

    // Fire-and-forget on purpose: these are raised on the input hook's thread, and a hook that
    // blocks is a hook Windows takes away.
    private void OnPressed() => _ = _setOpen(true);

    private void OnReleased() => _ = _setOpen(false);

    public void Dispose()
    {
        _input.Pressed -= OnPressed;
        _input.Released -= OnReleased;
        _input.Enabled = false;
        _registration.Dispose();
        (_input as IDisposable)?.Dispose();
    }
}
