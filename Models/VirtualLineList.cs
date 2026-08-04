using System.Collections;

namespace Warhorse.Models;

/// <summary>
/// Read-only <see cref="IList"/> over an <see cref="ITextDocument"/>.
///
/// WPF item virtualization only touches the indexer for realized items, so
/// binding this to an ItemsControl keeps just the visible lines in memory.
/// Enumerating it, sorting it or grouping it would defeat that — the reader
/// never does.
/// </summary>
public sealed class VirtualLineList : IList
{
    private readonly ITextDocument _document;

    public VirtualLineList(ITextDocument document)
    {
        _document = document;
    }

    public int Count => _document.LineCount;

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot { get; } = new();

    public string this[int index] => _document.GetLine(index);

    object? IList.this[int index]
    {
        get => _document.GetLine(index);
        set => throw new NotSupportedException();
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    /// <summary>Not supported: finding a line would mean scanning the whole document.</summary>
    public int IndexOf(object? value) => -1;

    public void CopyTo(Array array, int index) => throw new NotSupportedException();

    public IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return _document.GetLine(i);
        }
    }

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();
}
