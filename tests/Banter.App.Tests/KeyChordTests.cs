using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The push-to-talk chord, read out of a settings string. Its refusals matter: a hotkey that
/// quietly turns out not to be the one asked for is indistinguishable from a broken keyboard.
/// </summary>
public sealed class KeyChordTests
{
    private static KeyChord Parse(string text)
    {
        Assert.True(HotkeyChord.TryParse(text, out var chord, out var problem), problem);
        return chord;
    }

    [Fact]
    public void AModifiedKeyIsReadBack()
    {
        var chord = Parse("Ctrl+Shift+Space");

        Assert.Equal(0x20u, chord.Code);
        Assert.True(chord.Control);
        Assert.True(chord.Shift);
        Assert.False(chord.Alt);
        Assert.Equal("Ctrl+Shift+Space", chord.Display);
    }

    [Theory]
    [InlineData("a", 0x41u)]
    [InlineData("Z", 0x5Au)]
    [InlineData("7", 0x37u)]
    [InlineData("F1", 0x70u)]
    [InlineData("f12", 0x7Bu)]
    [InlineData("F24", 0x87u)]
    [InlineData("space", 0x20u)]
    [InlineData("PageDown", 0x22u)]
    public void KeysWorthHoldingAreUnderstood(string text, uint expected) =>
        Assert.Equal(expected, Parse(text).Code);

    [Theory]
    [InlineData("ctrl", "control")]
    [InlineData("alt", "option")]
    [InlineData("win", "cmd")]
    public void ModifiersHaveTheNamesPeopleActuallyType(string one, string other) =>
        Assert.Equal(Parse($"{one}+K").Display, Parse($"{other}+K").Display);

    [Fact]
    public void SpacingAndCaseDoNotMatter()
    {
        Assert.Equal(Parse("Ctrl+Shift+Space"), Parse("  ctrl + SHIFT +space "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Nonsense")]
    [InlineData("Ctrl+A+B")]
    [InlineData("F25")]
    [InlineData("F0")]
    [InlineData("+")]
    public void WhatCannotBeParsedIsRefusedWithAReason(string text)
    {
        Assert.False(HotkeyChord.TryParse(text, out _, out var problem));
        Assert.NotEqual("", problem);
    }

    [Fact]
    public void TheReasonSaysWhichPartWasNotUnderstood()
    {
        HotkeyChord.TryParse("Ctrl+Frobnicate", out _, out var problem);

        Assert.Contains("Frobnicate", problem);
    }

    [Fact]
    public void TheDisplayNameIsCanonicalRatherThanWhateverWasTyped()
    {
        // It is shown in the room as "hold X to talk", so it should read the same however the
        // setting was spelled.
        Assert.Equal("Ctrl+Alt+F9", Parse("alt+CTRL+f9").Display);
        Assert.Equal("Win+Space", Parse("super+SPACE").Display);
    }
}
