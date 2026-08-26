using Bantz.Speech;

namespace Banter.Voice.Tests;

/// <summary>Synthetic 16 kHz mono signal, so the gate is tested against known energy.</summary>
internal static class Pcm
{
    /// <summary>Loud enough to open the gate: a 300 Hz sine at 0.3 full scale is ~0.21 RMS.</summary>
    public const double Speech = 0.3;

    /// <summary>Room tone: 0.004 full scale is ~0.003 RMS, under the release threshold.</summary>
    public const double RoomTone = 0.004;

    public static byte[] Tone(TimeSpan duration, double amplitude, double frequency = 300)
    {
        var samples = (int)Math.Round(duration.TotalSeconds * PcmAudio.SpeechSampleRate);
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * frequency * i / PcmAudio.SpeechSampleRate)
                * amplitude * short.MaxValue);
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        return bytes;
    }

    public static byte[] Speaking(TimeSpan duration) => Tone(duration, Speech);

    public static byte[] Quiet(TimeSpan duration) => Tone(duration, RoomTone);

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static PcmAudio Audio(byte[] bytes) => new(bytes);

    public static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
}
