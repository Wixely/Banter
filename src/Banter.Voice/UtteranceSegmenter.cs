using Bantz.Speech;

namespace Banter.Voice;

/// <summary>
/// Cuts a continuous microphone stream into utterances — the always-listening half of PLAN §6's
/// capture modes. Audio arrives in whatever sizes the capture backend chooses; this re-frames it,
/// runs the hysteresis gate over each frame, and hands out one buffer per thing the user said.
///
/// <para><b>Not thread-safe.</b> Feed it from one thread — in practice the capture callback,
/// which is already serialised — the same single-writer discipline the room engine and the
/// client's view model use.</para>
/// </summary>
public sealed class UtteranceSegmenter
{
    private readonly VoiceActivityOptions _options;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _frameBytes;
    private readonly int _leadInFrames;
    private readonly int _trailingSilenceFrames;
    private readonly int _minSpeechFrames;
    private readonly int _maxUtteranceFrames;

    private readonly byte[] _partial;
    private int _partialFill;
    private readonly Queue<byte[]> _preRoll = new();
    private readonly MemoryStream _current = new();

    private bool _speaking;
    private int _silenceFrames;
    private int _voicedFrames;
    private int _utteranceFrames;
    private bool _continuation;

    public UtteranceSegmenter(
        VoiceActivityOptions? options = null,
        int sampleRate = PcmAudio.SpeechSampleRate,
        int channels = PcmAudio.SpeechChannels)
    {
        _options = options ?? VoiceActivityOptions.Default;
        _sampleRate = sampleRate;
        _channels = channels;
        _frameBytes = AudioLevels.BytesFor(_options.FrameDuration, sampleRate) * channels;
        if (_frameBytes <= 0)
        {
            throw new ArgumentException("Frame duration is shorter than one sample.", nameof(options));
        }

        _partial = new byte[_frameBytes];
        _leadInFrames = (int)Math.Ceiling(_options.LeadIn / _options.FrameDuration);
        _trailingSilenceFrames = Math.Max(1, (int)Math.Ceiling(_options.TrailingSilence / _options.FrameDuration));
        _minSpeechFrames = Math.Max(1, (int)Math.Ceiling(_options.MinSpeechDuration / _options.FrameDuration));
        _maxUtteranceFrames = Math.Max(1, (int)Math.Ceiling(_options.MaxUtterance / _options.FrameDuration));
    }

    /// <summary>Raised once per utterance, on the thread that called <see cref="Append"/>.</summary>
    public event Action<PcmAudio>? UtteranceCompleted;

    /// <summary>Whether the gate is currently open — what the "listening" indicator shows.</summary>
    public bool IsSpeaking => _speaking;

    /// <summary>Feeds captured audio in. Any length; framing is handled here.</summary>
    public void Append(ReadOnlySpan<byte> pcm)
    {
        while (!pcm.IsEmpty)
        {
            if (_partialFill == 0 && pcm.Length >= _frameBytes)
            {
                ProcessFrame(pcm[.._frameBytes]);
                pcm = pcm[_frameBytes..];
                continue;
            }

            var take = Math.Min(_frameBytes - _partialFill, pcm.Length);
            pcm[..take].CopyTo(_partial.AsSpan(_partialFill));
            _partialFill += take;
            pcm = pcm[take..];

            if (_partialFill == _frameBytes)
            {
                _partialFill = 0;
                ProcessFrame(_partial);
            }
        }
    }

    /// <summary>
    /// Ends the stream, emitting whatever is in flight if it qualifies. Every utterance leaves
    /// through <see cref="UtteranceCompleted"/>, including this one, so a caller has one place to
    /// handle them rather than a stream path and a forgotten end-of-stream path.
    /// </summary>
    public void Flush()
    {
        // A partial frame at the end is under 20 ms; it cannot carry the min-speech duration on
        // its own, but it can be the tail of a word already in the buffer.
        if (_partialFill > 0 && _speaking)
        {
            _current.Write(_partial.AsSpan(0, _partialFill));
        }

        _partialFill = 0;
        if (_speaking)
        {
            Complete(forced: false);
        }
    }

    /// <summary>Drops everything buffered without emitting — the hard mute switch.</summary>
    public void Reset()
    {
        _partialFill = 0;
        _preRoll.Clear();
        _current.SetLength(0);
        _speaking = false;
        _silenceFrames = 0;
        _voicedFrames = 0;
        _utteranceFrames = 0;
        _continuation = false;
    }

    private void ProcessFrame(ReadOnlySpan<byte> frame)
    {
        var rms = AudioLevels.Rms(frame);

        if (!_speaking)
        {
            if (rms < _options.OnsetRms)
            {
                PushPreRoll(frame);
                return;
            }

            _speaking = true;
            _silenceFrames = 0;
            _voicedFrames = 0;
            _utteranceFrames = 0;
            _current.SetLength(0);
            while (_preRoll.Count > 0)
            {
                _current.Write(_preRoll.Dequeue());
            }
        }

        _current.Write(frame);
        _utteranceFrames++;

        if (rms >= _options.ReleaseRms)
        {
            _voicedFrames++;
            _silenceFrames = 0;
        }
        else
        {
            _silenceFrames++;
        }

        if (_silenceFrames >= _trailingSilenceFrames)
        {
            Complete(forced: false);
        }
        else if (_utteranceFrames >= _maxUtteranceFrames && _silenceFrames == 0)
        {
            // Only cut someone who is still talking. Cutting during a silence run would sever a
            // sentence the trailing-silence rule was a few frames away from ending properly, and
            // leave a continuation holding nothing but the rest of the silence.
            Complete(forced: true);
        }
    }

    private void PushPreRoll(ReadOnlySpan<byte> frame)
    {
        if (_leadInFrames == 0)
        {
            return;
        }

        // Reuse the evicted buffer rather than allocating one per frame: in a quiet room this is
        // the path that runs fifty times a second, forever.
        var slot = _preRoll.Count >= _leadInFrames ? _preRoll.Dequeue() : new byte[_frameBytes];
        frame.CopyTo(slot);
        _preRoll.Enqueue(slot);
    }

    private void Complete(bool forced)
    {
        // A forced cut is the middle of a sentence, so the piece after it is speech by
        // construction however short it turns out to be — the min-speech gate exists to reject
        // door slams, not to reject the tail of something we already decided was a voice.
        var qualifies = _voicedFrames >= _minSpeechFrames || (_continuation && _voicedFrames > 0);

        if (qualifies)
        {
            var length = (int)_current.Length;

            // Give back the silence that ended the utterance, minus one lead-in's worth kept so
            // the last word does not sound clipped.
            var excessSilence = _silenceFrames - _leadInFrames;
            if (!forced && excessSilence > 0)
            {
                length = Math.Max(0, length - (excessSilence * _frameBytes));
            }

            if (length > 0)
            {
                UtteranceCompleted?.Invoke(new PcmAudio(_current.ToArray().AsMemory(0, length), _sampleRate, _channels));
            }
        }

        _current.SetLength(0);
        _preRoll.Clear();
        _silenceFrames = 0;
        _voicedFrames = 0;
        _utteranceFrames = 0;
        _speaking = forced;
        _continuation = forced;
    }
}
