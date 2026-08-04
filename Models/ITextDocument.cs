namespace Warhorse.Models;

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
}
