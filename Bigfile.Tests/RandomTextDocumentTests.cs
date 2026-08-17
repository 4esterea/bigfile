using Bigfile.Models;

namespace Bigfile.Tests;

/// <summary>
/// The generated document is addressable rather than stored, and the virtualized
/// reader relies on that: a recycled container asks for the same line again and
/// must get the same text back.
/// </summary>
public class RandomTextDocumentTests
{
    [Fact]
    public void The_same_line_always_comes_back_the_same()
    {
        var document = new RandomTextDocument(1_000);

        Assert.Equal(document.GetLine(0), document.GetLine(0));
        Assert.Equal(document.GetLine(742), document.GetLine(742));
    }

    [Fact]
    public void A_sweep_agrees_with_random_access()
    {
        var document = new RandomTextDocument(100);

        foreach (var (index, text) in document.ReadFrom(10))
        {
            Assert.Equal(document.GetLine(index), text);
        }
    }

    [Fact]
    public void A_sweep_starts_where_it_was_asked_to_and_ends_with_the_document()
    {
        var document = new RandomTextDocument(50);
        var swept = document.ReadFrom(48).ToArray();

        Assert.Equal([48, 49], swept.Select(line => line.Index));
    }

    [Fact]
    public void Lines_are_numbered_so_navigation_is_verifiable_by_eye()
    {
        var document = new RandomTextDocument(1_000);

        Assert.StartsWith("1 ", document.GetLine(0).TrimStart());
        Assert.StartsWith("1000 ", document.GetLine(999).TrimStart());
    }

    [Fact]
    public void Lines_are_made_of_english_words_only()
    {
        var document = new RandomTextDocument(5_000);

        for (var index = 0; index < document.LineCount; index++)
        {
            // Past the line number the line is words only — no digits, no
            // punctuation and nothing outside the English alphabet.
            var words = document.GetLine(index)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..];

            Assert.NotEmpty(words);
            Assert.All(words, word => Assert.All(word, letter => Assert.InRange(letter, 'a', 'z')));
        }
    }

    [Fact]
    public void Costs_nothing_to_declare_a_huge_document()
    {
        var document = new RandomTextDocument(100_000_000);

        Assert.Equal(100_000_000, document.LineCount);
        Assert.NotEmpty(document.GetLine(99_999_999));
    }

    [Fact]
    public void Refuses_lines_that_do_not_exist()
    {
        var document = new RandomTextDocument(10);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.GetLine(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.GetLine(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RandomTextDocument(-1));
    }
}
