using System.Collections;

namespace Bigfile.Models;

/// <summary>
/// Read-only <see cref="IList"/> over an <see cref="ITextDocument"/>, either
/// the whole text or a subset of its lines.
///
/// WPF item virtualization only touches the indexer for realized items, so
/// binding this to an ItemsControl keeps just the visible lines in memory.
/// Enumerating it, sorting it or grouping it would defeat that — the reader
/// never does.
/// </summary>
public sealed class VirtualLineList : IList
{
    private readonly ITextDocument _document;

    /// <summary>
    /// Document line behind each row, or null when the list is the whole
    /// document. The filtered view stores four bytes per matching line, which
    /// stays small next to the text it points at.
    /// </summary>
    private readonly int[]? _map;

    public VirtualLineList(ITextDocument document, int[]? map = null)
    {
        _document = document;
        _map = map;
    }

    public int Count => _map?.Length ?? _document.LineCount;

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot { get; } = new();

    public string this[int index] => _document.GetLine(SourceLine(index));

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    /// <summary>Maps a row back to its line in the document.</summary>
    public int SourceLine(int index) => _map?[index] ?? index;

    /// <summary>
    /// The lines this view shows, in ascending order, or null when it shows the
    /// whole document. Used to save a filtered view.
    /// </summary>
    public IReadOnlyList<int>? SourceLines => _map;

    public bool Contains(object? value) => IndexOf(value) >= 0;

    /// <summary>Not supported: finding a line would mean scanning the whole document.</summary>
    public int IndexOf(object? value) => -1;

    public void CopyTo(Array array, int index) => throw new NotSupportedException();

    public IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();
}
