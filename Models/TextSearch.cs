namespace Bigfile.Models;

/// <summary>
/// Search over an <see cref="ITextDocument"/>, down to the position of a match
/// inside its line.
///
/// Every method sweeps the document through <see cref="ITextDocument.ReadFrom"/>
/// and keeps at most one line in memory, so searching a 50 GB file costs the
/// same as searching a small one — only time, which is why each takes a
/// <see cref="CancellationToken"/> and is meant to run off the UI thread.
///
/// This is the whole search surface the rest of the app needs: how the text is
/// actually read is free to change behind it.
/// </summary>
public static class TextSearch
{
    /// <summary>Returned as a line index when no line matches.</summary>
    public const int NotFound = -1;

    /// <summary>
    /// Lines read at once when searching backwards. A sweep only moves forward,
    /// so a backward search reads a window and keeps its last match.
    /// </summary>
    private const int BackwardWindowLines = 4096;

    /// <summary>
    /// Where a match sits: the line and the offset within it. Stepping needs both,
    /// because a line can hold several occurrences and the reader marks the one
    /// the user is on.
    /// </summary>
    public readonly record struct Hit(int Line, int Column)
    {
        public static readonly Hit None = new(NotFound, 0);

        public bool Found => Line != NotFound;
    }

    /// <summary>
    /// First occurrence at or after (<paramref name="startLine"/>,
    /// <paramref name="startColumn"/>), wrapping around the end of the document.
    /// </summary>
    public static Hit FindNext(
        ITextDocument document,
        string query,
        int startLine,
        int startColumn = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query) || document.LineCount == 0)
        {
            return Hit.None;
        }

        // A position outside the document is how the caller asks to start over.
        if (startLine < 0 || startLine >= document.LineCount)
        {
            startLine = 0;
            startColumn = 0;
        }

        var hit = FirstFrom(
            document, query, startLine, Math.Max(startColumn, 0),
            document.LineCount - 1, cancellationToken);

        if (hit.Found)
        {
            return hit;
        }

        // Wrapping re-reads the start line whole, which is where the occurrences
        // before the starting column live.
        return FirstFrom(document, query, 0, 0, startLine, cancellationToken);
    }

    /// <summary>
    /// Last occurrence at or before (<paramref name="endLine"/>,
    /// <paramref name="endColumn"/>), wrapping around the start of the document.
    /// </summary>
    public static Hit FindPrevious(
        ITextDocument document,
        string query,
        int endLine,
        int endColumn = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query) || document.LineCount == 0)
        {
            return Hit.None;
        }

        // A column before the start of a line means the line above it, whole.
        if (endColumn < 0)
        {
            endLine--;
            endColumn = int.MaxValue;
        }

        if (endLine < 0 || endLine >= document.LineCount)
        {
            endLine = document.LineCount - 1;
            endColumn = int.MaxValue;
        }

        var hit = LastUpTo(document, query, 0, endLine, endColumn, cancellationToken);

        if (hit.Found)
        {
            return hit;
        }

        // Wrapping to the bottom likewise re-reads the end line whole.
        return LastUpTo(
            document, query, endLine, document.LineCount - 1, int.MaxValue, cancellationToken);
    }

    /// <summary>
    /// Indexes of every line containing <paramref name="query"/>, for the
    /// filtered view. Only the indexes are kept — four bytes per match — so the
    /// result stays small next to the text it points at.
    /// </summary>
    /// <param name="limit">Stops once this many matches are collected.</param>
    public static int[] FindAll(
        ITextDocument document,
        string query,
        int limit,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query) || document.LineCount == 0 || limit <= 0)
        {
            return [];
        }

        var matches = new List<int>();
        var lineCount = document.LineCount;
        var interval = ProgressPacing.Interval(lineCount);

        foreach (var (index, text) in document.ReadFrom(0))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Matches(text, query))
            {
                matches.Add(index);

                if (matches.Count == limit)
                {
                    break;
                }
            }

            if (progress is not null && index % interval == 0)
            {
                progress.Report((double)index / lineCount);
            }
        }

        progress?.Report(1);
        return matches.ToArray();
    }

    /// <summary>
    /// First occurrence of <paramref name="query"/> in <paramref name="text"/> at
    /// or after <paramref name="from"/>, or -1. Used to step within one line the
    /// reader already has, without sweeping the document.
    /// </summary>
    public static int IndexIn(string text, string query, int from)
    {
        if (string.IsNullOrEmpty(query) || from > text.Length)
        {
            return NotFound;
        }

        return text.IndexOf(query, Math.Max(from, 0), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Last occurrence of <paramref name="query"/> in <paramref name="text"/>
    /// starting at or before <paramref name="limit"/>, or -1.
    /// </summary>
    public static int LastIndexIn(string text, string query, int limit)
    {
        if (string.IsNullOrEmpty(query) || text.Length == 0 || limit < 0)
        {
            return NotFound;
        }

        // LastIndexOf takes the position a match must end by, while the limit says
        // where it may start, so the two differ by the length of the query. Adding
        // it before clamping would overflow on a limit of int.MaxValue.
        var start = limit >= text.Length
            ? text.Length - 1
            : Math.Min(limit + query.Length - 1, text.Length - 1);

        return text.LastIndexOf(query, start, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// First occurrence in the inclusive line range, starting at
    /// <paramref name="firstColumn"/> on the first line, scanning forward.
    /// </summary>
    private static Hit FirstFrom(
        ITextDocument document,
        string query,
        int firstLine,
        int firstColumn,
        int lastLine,
        CancellationToken cancellationToken)
    {
        if (firstLine > lastLine)
        {
            return Hit.None;
        }

        foreach (var (index, text) in document.ReadFrom(firstLine))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (index > lastLine)
            {
                break;
            }

            var column = IndexIn(text, query, index == firstLine ? firstColumn : 0);

            if (column != NotFound)
            {
                return new Hit(index, column);
            }
        }

        return Hit.None;
    }

    /// <summary>
    /// Last occurrence in the inclusive line range, no further than
    /// <paramref name="lastColumn"/> on its final line. The range is walked in
    /// windows from the end backwards, each window scanned forward, so no more
    /// than one window is read past the match.
    /// </summary>
    private static Hit LastUpTo(
        ITextDocument document,
        string query,
        int firstLine,
        int lastLine,
        int lastColumn,
        CancellationToken cancellationToken)
    {
        for (var end = lastLine; end >= firstLine; end -= BackwardWindowLines)
        {
            var from = Math.Max(firstLine, end - BackwardWindowLines + 1);
            var hit = LastMatchIn(
                document, query, from, end, lastLine, lastColumn, cancellationToken);

            if (hit.Found)
            {
                return hit;
            }
        }

        return Hit.None;
    }

    /// <summary>
    /// Last occurrence within one window. The column cap applies to
    /// <paramref name="capLine"/> only; every other line is searched whole.
    /// </summary>
    private static Hit LastMatchIn(
        ITextDocument document,
        string query,
        int first,
        int last,
        int capLine,
        int capColumn,
        CancellationToken cancellationToken)
    {
        var found = Hit.None;

        foreach (var (index, text) in document.ReadFrom(first))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (index > last)
            {
                break;
            }

            var column = LastIndexIn(
                text, query, index == capLine ? capColumn : int.MaxValue);

            if (column != NotFound)
            {
                found = new Hit(index, column);
            }
        }

        return found;
    }

    private static bool Matches(string text, string query) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase);
}
