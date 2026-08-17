namespace Bigfile.Models;

/// <summary>
/// Random access to the lines of a text that stays on disk.
/// Implementations must not hold the whole text in memory.
/// </summary>
public interface ITextDocument : IDisposable
{
    /// <summary>Total number of lines.</summary>
    int LineCount { get; }

    /// <summary>Reads a single line by its zero-based index.</summary>
    string GetLine(int index);

    /// <summary>
    /// Lines from <paramref name="startLine"/> to the end of the document,
    /// paired with their zero-based index, for sweeping the text in the
    /// background — searching or filtering.
    ///
    /// Lazily evaluated, so a caller that stops early pays only for what it
    /// read. Each enumeration owns its reading state, which is what makes a
    /// background sweep safe while the UI thread calls <see cref="GetLine"/>.
    /// </summary>
    IEnumerable<(int Index, string Text)> ReadFrom(int startLine);
}
