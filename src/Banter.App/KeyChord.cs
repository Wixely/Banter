namespace Banter.App;

/// <summary>
/// A parsed hotkey chord: the modifiers, and the virtual-key code of the key itself.
///
/// <para>Codes are Windows virtual-key values because that is what the global-input backend takes.
/// Parsing lives here rather than in the head so it can be tested without a keyboard, and so a
/// second head that wants the same setting string does not write a second parser.</para>
/// </summary>
public readonly record struct KeyChord(uint Code, bool Control, bool Shift, bool Alt, bool Windows, string Display);

/// <summary>Reads a hotkey out of a settings string such as <c>Ctrl+Shift+Space</c>.</summary>
public static class HotkeyChord
{
    /// <summary>
    /// Parses <paramref name="text"/>, or returns false with a reason.
    ///
    /// <para>A reason rather than a silent fallback: a hotkey that quietly is not the one asked
    /// for reads as a broken keyboard, and the user has no way to tell which of the two it is.</para>
    /// </summary>
    public static bool TryParse(string text, out KeyChord chord, out string problem)
    {
        chord = default;
        problem = "";

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            problem = "no key given";
            return false;
        }

        bool control = false, shift = false, alt = false, windows = false;
        string? keyName = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": control = true; break;
                case "shift": shift = true; break;
                case "alt" or "option": alt = true; break;
                case "win" or "windows" or "cmd" or "super": windows = true; break;
                default:
                    if (keyName is not null)
                    {
                        problem = $"more than one key ('{keyName}' and '{part}')";
                        return false;
                    }

                    keyName = part;
                    break;
            }
        }

        if (keyName is null)
        {
            problem = "modifiers only, with no key to press";
            return false;
        }

        if (!TryCode(keyName, out var code))
        {
            problem = $"'{keyName}' is not a key this understands";
            return false;
        }

        var display = string.Concat(
            control ? "Ctrl+" : "",
            shift ? "Shift+" : "",
            alt ? "Alt+" : "",
            windows ? "Win+" : "",
            Canonical(keyName));

        chord = new KeyChord(code, control, shift, alt, windows, display);
        return true;
    }

    /// <summary>
    /// Virtual-key code for a key name. Letters, digits, function keys and the handful of keys
    /// worth holding down — a push-to-talk key is held, so most of the keyboard is a poor choice
    /// for one and there is no value in accepting all of it.
    /// </summary>
    private static bool TryCode(string name, out uint code)
    {
        code = 0;

        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                code = c;
                return true;
            }

            return false;
        }

        if (name.Length is 2 or 3
            && (name[0] is 'f' or 'F')
            && int.TryParse(name.AsSpan(1), out var n)
            && n is >= 1 and <= 24)
        {
            code = (uint)(0x70 + n - 1);                    // VK_F1 .. VK_F24
            return true;
        }

        code = name.ToLowerInvariant() switch
        {
            "space" or "spacebar" => 0x20,
            "tab" => 0x09,
            "capslock" => 0x14,
            "insert" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "scrolllock" => 0x91,
            "pause" => 0x13,
            _ => 0,
        };

        return code != 0;
    }

    private static string Canonical(string name)
    {
        if (name.Length == 1)
        {
            return name.ToUpperInvariant();
        }

        if ((name[0] is 'f' or 'F') && name.Length is 2 or 3 && int.TryParse(name.AsSpan(1), out var n))
        {
            return $"F{n}";
        }

        return char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
    }
}
