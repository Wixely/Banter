using System.Text;

namespace Banter.Voice;

public sealed record SentenceSegmenterOptions
{
    public static SentenceSegmenterOptions Default { get; } = new();

    /// <summary>
    /// Length at which a run of text is broken even with no punctuation in it. Models produce
    /// unpunctuated walls of text often enough that without this an agent can stream for a
    /// minute and be spoken only once it stops.
    /// </summary>
    public int MaxCharacters { get; init; } = 240;

    /// <summary>
    /// Words that end in a full stop without ending a sentence. Short on purpose: every entry is
    /// a word that can also legitimately end one, and the cost of a wrong split is a half-spoken
    /// clause, while the cost of a missed one is a slightly long sentence.
    /// </summary>
    public IReadOnlyCollection<string> Abbreviations { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "approx", "fig", "al",
        };
}

/// <summary>
/// Cuts streamed text into sentences, so PLAN §6 can speak an agent's reply as its deltas
/// complete rather than after <c>MSG_STREAM_END</c>. Waiting for the end puts the whole
/// generation time in front of the first sound; on a long reply that is the difference between an
/// agent that answers and one that appears to have hung.
///
/// <para>A sentence is only emitted once something is seen <i>after</i> its terminator: until
/// then "3." might be the start of "3.5", and speaking half a number is worse than waiting one
/// delta. <see cref="Flush"/> is what closes the last one.</para>
///
/// <para>Not thread-safe; feed it from the one place handling a stream's deltas.</para>
/// </summary>
public sealed class SentenceSegmenter
{
    private readonly SentenceSegmenterOptions _options;
    private readonly StringBuilder _buffer = new();

    public SentenceSegmenter(SentenceSegmenterOptions? options = null) =>
        _options = options ?? SentenceSegmenterOptions.Default;

    /// <summary>Whether anything is held back waiting for more text.</summary>
    public bool HasPending => _buffer.Length > 0;

    /// <summary>Adds a delta and returns whatever sentences that completed — usually none or one.</summary>
    public IReadOnlyList<string> Append(string delta)
    {
        if (delta.Length > 0)
        {
            _buffer.Append(delta);
        }

        var sentences = new List<string>();
        var text = _buffer.ToString();
        var start = 0;
        var index = 0;

        while (index < text.Length)
        {
            if (!IsTerminator(text[index]))
            {
                index++;
                continue;
            }

            // "..." and "?!" end one sentence, not three.
            var last = index;
            while (last + 1 < text.Length && IsTerminator(text[last + 1]))
            {
                last++;
            }

            var after = last + 1;
            while (after < text.Length && IsClosing(text[after]))
            {
                after++;
            }

            if (after >= text.Length)
            {
                break;                                      // nothing after it yet: cannot tell
            }

            if (!char.IsWhiteSpace(text[after]) || (text[last] == '.' && !EndsSentence(text, index)))
            {
                index = last + 1;
                continue;
            }

            // An ellipsis is a pause as often as an ending — "Well... mostly." is one sentence.
            // What follows tells them apart, so this is the one case worth waiting a word for.
            if (last > index || text[index] == '…')
            {
                var probe = after;
                while (probe < text.Length && char.IsWhiteSpace(text[probe]))
                {
                    probe++;
                }

                if (probe >= text.Length)
                {
                    break;
                }

                if (char.IsLower(text[probe]))
                {
                    index = last + 1;
                    continue;
                }
            }

            var sentence = text[start..after].Trim();
            if (sentence.Length > 0)
            {
                sentences.Add(sentence);
            }

            start = after;
            index = after;
        }

        _buffer.Remove(0, start);
        DrainOverlong(sentences);
        return sentences;
    }

    /// <summary>
    /// Returns whatever is left, at the end of a stream. Null when there is nothing — a stream
    /// that ended on a terminator has already had its last sentence handed over.
    /// </summary>
    public string? Flush()
    {
        var rest = _buffer.ToString().Trim();
        _buffer.Clear();
        return rest.Length == 0 ? null : rest;
    }

    /// <summary>Breaks text that has run past the limit without punctuating itself.</summary>
    private void DrainOverlong(List<string> sentences)
    {
        while (_buffer.Length > _options.MaxCharacters)
        {
            var text = _buffer.ToString(0, _options.MaxCharacters);

            // At a word boundary where there is one: cutting mid-word is audible, and a run with
            // no spaces at all is not speech anyway.
            var cut = text.LastIndexOf(' ');
            if (cut <= 0)
            {
                cut = _options.MaxCharacters;
            }

            var chunk = _buffer.ToString(0, cut).Trim();
            if (chunk.Length > 0)
            {
                sentences.Add(chunk);
            }

            _buffer.Remove(0, cut);
        }
    }

    private static bool IsTerminator(char c) => c is '.' or '!' or '?' or '…' or '。' or '！' or '？';

    /// <summary>Quotes and brackets that belong to the sentence they follow.</summary>
    private static bool IsClosing(char c) => c is '"' or '\'' or ')' or ']' or '»' or '”' or '’';

    /// <summary>
    /// Whether the full stop at <paramref name="index"/> ends a sentence rather than an
    /// abbreviation or an initial.
    /// </summary>
    private bool EndsSentence(string text, int index)
    {
        var end = index;
        var begin = index;
        while (begin > 0 && char.IsLetter(text[begin - 1]))
        {
            begin--;
        }

        if (begin == end)
        {
            return true;                                    // no word before it at all
        }

        var word = text[begin..end];

        // A single letter is an initial — "J. R. R." — or the tail of "e.g."
        return word.Length > 1 && !_options.Abbreviations.Contains(word);
    }
}
