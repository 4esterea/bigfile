using System.Text;

namespace Bigfile.Models;

/// <summary>
/// A synthetic document that is never stored anywhere.
///
/// Every line is derived from the seed and its own index, so <see cref="GetLine"/>
/// is a pure function: the same line always comes back the same, which is what
/// lets the reader realize, recycle and re-realize containers freely. The whole
/// document costs a few bytes of state no matter how many lines it has, and
/// there is nothing to index, so it opens instantly.
/// </summary>
public sealed class RandomTextDocument : ITextDocument
{
    private const int MinWords = 1;
    private const int MaxWords = 28;

    /// <summary>
    /// Everyday English words, ASCII only. Common ones on purpose: a generated
    /// document is something to scroll and search through, so the words have to be
    /// easy to recognise on screen and easy to type into the search box.
    /// </summary>
    private static readonly string[] Words =
    [
        "about", "after", "again", "always", "answer", "around", "before", "better",
        "between", "bridge", "bright", "build", "carry", "change", "circle", "city",
        "clear", "close", "cold", "colour", "corner", "country", "cover", "detail",
        "different", "double", "early", "earth", "empty", "enough", "every", "family",
        "father", "field", "figure", "finish", "first", "follow", "forest", "friend",
        "garden", "green", "ground", "happy", "heavy", "history", "house", "however",
        "island", "journey", "kitchen", "language", "large", "later", "learn", "letter",
        "level", "light", "listen", "little", "market", "matter", "measure", "middle",
        "modern", "moment", "morning", "mother", "mountain", "narrow", "nature", "never",
        "night", "north", "notice", "number", "object", "ocean", "office", "often",
        "order", "other", "paper", "people", "perhaps", "person", "picture", "place",
        "point", "power", "problem", "public", "quiet", "reason", "record", "remember",
        "river", "sample", "school", "season", "second", "sentence", "several", "should",
        "silver", "simple", "since", "single", "small", "sound", "south", "space",
        "start", "still", "stone", "story", "street", "strong", "summer", "table",
        "teacher", "there", "thing", "think", "third", "though", "through", "today",
        "together", "under", "until", "usual", "value", "village", "voice", "water",
        "weather", "where", "which", "while", "white", "window", "winter", "without",
        "wonder", "world", "would", "write", "yellow", "young"
    ];

    private readonly ulong _seed;

    /// <summary>Digits the largest line number takes, so numbers line up.</summary>
    private readonly int _numberWidth;

    public RandomTextDocument(int lineCount, ulong seed = 0x5DEECE66D)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lineCount);

        LineCount = lineCount;
        _seed = seed;
        _numberWidth = Math.Max(lineCount, 1).ToString().Length;
    }

    public int LineCount { get; }

    public string GetLine(int index)
    {
        if (index < 0 || index >= LineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var random = Mix(_seed + (uint)index);
        var wordCount = MinWords + (int)(random % (MaxWords - MinWords + 1));

        // The line number makes scrolling and search results verifiable at a
        // glance, which is the point of a generated document.
        var builder = new StringBuilder()
            .Append((index + 1).ToString().PadLeft(_numberWidth))
            .Append("  ");

        for (var i = 0; i < wordCount; i++)
        {
            random = Mix(random);

            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Words[(int)(random % (ulong)Words.Length)]);
        }

        return builder.ToString();
    }

    public IEnumerable<(int Index, string Text)> ReadFrom(int startLine)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startLine);

        // Generating is stateless, so a sweep is just the indexer in a loop and
        // any number of sweeps can run at once.
        for (var index = startLine; index < LineCount; index++)
        {
            yield return (index, GetLine(index));
        }
    }

    /// <summary>SplitMix64 — cheap, seekable and good enough to look random.</summary>
    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    public void Dispose()
    {
        // Nothing is held: no stream, no buffers, no text.
    }
}
