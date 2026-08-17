using Bigfile.Models;

namespace Bigfile.Tests;

/// <summary>
/// The bridge WPF binds to. What matters is that it reads a line only when asked
/// for one, and that the filtered view maps rows back to their document lines.
/// </summary>
public class VirtualLineListTests
{
    /// <summary>Records which lines were actually asked for.</summary>
    private sealed class CountingDocument(int lineCount) : ITextDocument
    {
        public List<int> Requested { get; } = [];

        public int LineCount => lineCount;

        public string GetLine(int index)
        {
            Requested.Add(index);
            return $"line {index}";
        }

        public IEnumerable<(int Index, string Text)> ReadFrom(int startLine)
        {
            for (var i = startLine; i < lineCount; i++)
            {
                yield return (i, GetLine(i));
            }
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Counting_the_whole_document_reads_nothing()
    {
        var document = new CountingDocument(1_000_000);
        var list = new VirtualLineList(document);

        Assert.Equal(1_000_000, list.Count);
        Assert.Empty(document.Requested);
    }

    [Fact]
    public void An_indexed_row_reads_exactly_that_line()
    {
        var document = new CountingDocument(1_000_000);
        var list = new VirtualLineList(document);

        Assert.Equal("line 500", list[500]);
        Assert.Equal([500], document.Requested);
    }

    [Fact]
    public void A_filtered_row_maps_back_to_its_document_line()
    {
        var document = new CountingDocument(1_000);
        var list = new VirtualLineList(document, [7, 42, 99]);

        Assert.Equal(3, list.Count);
        Assert.Equal("line 42", list[1]);
        Assert.Equal(42, list.SourceLine(1));
        Assert.Equal([7, 42, 99], list.SourceLines);
    }

    [Fact]
    public void The_list_is_read_only()
    {
        var list = new VirtualLineList(new CountingDocument(10));

        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add("x"));
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        Assert.Throws<NotSupportedException>(list.Clear);
    }
}
