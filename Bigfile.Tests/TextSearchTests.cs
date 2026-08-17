using Bigfile.Models;

namespace Bigfile.Tests;

/// <summary>
/// Search is defined over the document contract, so these tests use an in-memory
/// document: what is exercised is the stepping, the wrapping and the column
/// arithmetic, none of which care where the lines came from.
/// </summary>
public class TextSearchTests
{
    /// <summary>An <see cref="ITextDocument"/> over an array of lines.</summary>
    private sealed class StringDocument(params string[] lines) : ITextDocument
    {
        public int LineCount => lines.Length;

        public string GetLine(int index) => lines[index];

        public IEnumerable<(int Index, string Text)> ReadFrom(int startLine)
        {
            for (var i = startLine; i < lines.Length; i++)
            {
                yield return (i, lines[i]);
            }
        }

        public void Dispose()
        {
        }
    }

    private static readonly StringDocument Document = new(
        "alpha beta",   // beta at 6
        "gamma",
        "beta beta",    // beta at 0 and 5
        "delta");

    [Fact]
    public void Finds_the_first_match_with_its_column()
    {
        var hit = TextSearch.FindNext(Document, "beta", 0);

        Assert.True(hit.Found);
        Assert.Equal(0, hit.Line);
        Assert.Equal(6, hit.Column);
    }

    [Fact]
    public void Steps_to_the_second_occurrence_of_the_same_line()
    {
        // Standing on the first "beta" of line 2 and stepping forward must not
        // skip the second one, which is the whole point of tracking columns.
        var hit = TextSearch.FindNext(Document, "beta", 2, 1);

        Assert.Equal(2, hit.Line);
        Assert.Equal(5, hit.Column);
    }

    [Fact]
    public void Leaves_the_line_once_its_occurrences_run_out()
    {
        var hit = TextSearch.FindNext(Document, "beta", 0, 7);

        Assert.Equal(2, hit.Line);
        Assert.Equal(0, hit.Column);
    }

    [Fact]
    public void Wraps_around_the_end_of_the_document()
    {
        var hit = TextSearch.FindNext(Document, "beta", 3);

        Assert.Equal(0, hit.Line);
        Assert.Equal(6, hit.Column);
    }

    [Fact]
    public void Ignores_case()
    {
        Assert.Equal(new TextSearch.Hit(0, 6), TextSearch.FindNext(Document, "BeTa", 0));
    }

    [Fact]
    public void Reports_nothing_when_there_is_nothing()
    {
        Assert.False(TextSearch.FindNext(Document, "zeta", 0).Found);
        Assert.False(TextSearch.FindPrevious(Document, "zeta", 3).Found);
        Assert.False(TextSearch.FindNext(Document, string.Empty, 0).Found);
    }

    [Fact]
    public void Finds_the_last_occurrence_backwards()
    {
        var hit = TextSearch.FindPrevious(Document, "beta", 2);

        Assert.Equal(2, hit.Line);
        Assert.Equal(5, hit.Column);
    }

    [Fact]
    public void Steps_back_within_a_line()
    {
        var hit = TextSearch.FindPrevious(Document, "beta", 2, 4);

        Assert.Equal(2, hit.Line);
        Assert.Equal(0, hit.Column);
    }

    [Fact]
    public void Stepping_back_off_the_start_of_a_line_moves_up()
    {
        var hit = TextSearch.FindPrevious(Document, "beta", 2, -1);

        Assert.Equal(0, hit.Line);
        Assert.Equal(6, hit.Column);
    }

    [Fact]
    public void Wraps_around_the_start_of_the_document()
    {
        var hit = TextSearch.FindPrevious(Document, "beta", 0, 5);

        Assert.Equal(2, hit.Line);
        Assert.Equal(5, hit.Column);
    }

    [Fact]
    public void Searching_backwards_crosses_its_windows()
    {
        // More lines than the backward window, with the only match at the top:
        // the windowed backward sweep has to walk all the way up to it.
        var lines = new string[6_000];
        Array.Fill(lines, "nothing here");
        lines[0] = "the needle";

        var document = new StringDocument(lines);
        var hit = TextSearch.FindPrevious(document, "needle", lines.Length - 1);

        Assert.Equal(0, hit.Line);
        Assert.Equal(4, hit.Column);
    }

    [Fact]
    public void Collects_matching_lines_up_to_the_limit()
    {
        Assert.Equal([0, 2], TextSearch.FindAll(Document, "beta", 10));
        Assert.Equal([0], TextSearch.FindAll(Document, "beta", 1));
        Assert.Empty(TextSearch.FindAll(Document, "beta", 0));
    }

    [Fact]
    public void Reports_progress_for_a_full_sweep()
    {
        var reports = new List<double>();

        TextSearch.FindAll(Document, "zeta", 10, new SynchronousProgress(reports.Add));

        Assert.NotEmpty(reports);
        Assert.Equal(1, reports[^1]);
    }

    [Fact]
    public void Single_line_stepping_matches_the_sweep()
    {
        Assert.Equal(6, TextSearch.IndexIn("alpha beta", "beta", 0));
        Assert.Equal(TextSearch.NotFound, TextSearch.IndexIn("alpha beta", "beta", 7));
        Assert.Equal(TextSearch.NotFound, TextSearch.IndexIn("alpha", "beta", 99));

        Assert.Equal(5, TextSearch.LastIndexIn("beta beta", "beta", int.MaxValue));
        Assert.Equal(0, TextSearch.LastIndexIn("beta beta", "beta", 4));
        Assert.Equal(TextSearch.NotFound, TextSearch.LastIndexIn(string.Empty, "beta", 0));

        // A match may start at the limit and run past it — that is what makes
        // "the occurrence I am standing on" findable when stepping back.
        Assert.Equal(2, TextSearch.LastIndexIn("abcd", "cd", 2));
    }

    /// <summary>
    /// <see cref="Progress{T}"/> posts to a context, which a test thread does not
    /// have; this reports inline so the assertions can see the reports.
    /// </summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
