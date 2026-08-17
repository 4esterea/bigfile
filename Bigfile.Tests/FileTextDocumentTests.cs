using System.Text;
using Bigfile.Models;

namespace Bigfile.Tests;

/// <summary>
/// The indexer is where the awkward cases live: block boundaries, missing final
/// newlines, byte order marks, encodings and lines longer than a screen.
/// </summary>
public class FileTextDocumentTests
{
    /// <summary>Mirrors FileTextDocument.LinesPerBlock, the interesting boundary.</summary>
    private const int LinesPerBlock = 512;

    private static async Task<ITextDocument> OpenAsync(TempTextFile file) =>
        await FileTextDocument.OpenAsync(file.Path);

    private static string[] Sweep(ITextDocument document) =>
        document.ReadFrom(0).Select(line => line.Text).ToArray();

    [Fact]
    public async Task Reads_lines_separated_by_lf()
    {
        using var file = TempTextFile.FromText("alpha\nbeta\ngamma\n");
        using var document = await OpenAsync(file);

        Assert.Equal(3, document.LineCount);
        Assert.Equal(["alpha", "beta", "gamma"], Sweep(document));
        Assert.Equal("beta", document.GetLine(1));
    }

    [Fact]
    public async Task Drops_the_cr_of_crlf()
    {
        using var file = TempTextFile.FromText("alpha\r\nbeta\r\n");
        using var document = await OpenAsync(file);

        Assert.Equal(2, document.LineCount);
        Assert.Equal(["alpha", "beta"], Sweep(document));
        Assert.Equal("alpha", document.GetLine(0));
    }

    [Fact]
    public async Task Text_after_the_last_newline_is_a_line()
    {
        using var file = TempTextFile.FromText("alpha\nbeta");
        using var document = await OpenAsync(file);

        Assert.Equal(2, document.LineCount);
        Assert.Equal("beta", document.GetLine(1));
        Assert.Equal(["alpha", "beta"], Sweep(document));
    }

    [Fact]
    public async Task An_empty_file_has_no_lines()
    {
        using var file = TempTextFile.FromText(string.Empty);
        using var document = await OpenAsync(file);

        Assert.Equal(0, document.LineCount);
        Assert.Empty(Sweep(document));
    }

    [Fact]
    public async Task A_byte_order_mark_is_not_part_of_the_first_line()
    {
        using var file = TempTextFile.FromBytes([0xEF, 0xBB, 0xBF, .. "alpha\nbeta\n"u8]);
        using var document = await OpenAsync(file);

        Assert.Equal(2, document.LineCount);
        Assert.Equal("alpha", document.GetLine(0));
    }

    [Fact]
    public async Task Utf16_is_rejected_rather_than_misread()
    {
        using var file = TempTextFile.FromBytes([0xFF, 0xFE, 0x61, 0x00, 0x0A, 0x00]);

        await Assert.ThrowsAsync<NotSupportedException>(() => OpenAsync(file));
    }

    [Fact]
    public async Task Cr_only_line_endings_are_lines_not_one_giant_line()
    {
        using var file = TempTextFile.FromText("alpha\rbeta\rgamma");
        using var document = await OpenAsync(file);

        Assert.Equal(3, document.LineCount);
        Assert.Equal(["alpha", "beta", "gamma"], Sweep(document));
    }

    [Fact]
    public async Task Non_utf8_bytes_decode_without_replacement_marks()
    {
        // Bytes 0xC0..0xCF are a valid single-byte codepage sequence and invalid
        // UTF-8, so they must be decoded through the ANSI fallback. Which
        // characters they become depends on the machine's code page; that none of
        // them is a replacement mark does not.
        var bytes = new byte[16];

        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(0xC0 + i);
        }

        using var file = TempTextFile.FromBytes([.. bytes, (byte)'\n']);
        using var document = await OpenAsync(file);

        var line = document.GetLine(0);

        Assert.Equal(bytes.Length, line.Length);
        Assert.DoesNotContain('�', line);
    }

    [Theory]
    [InlineData(LinesPerBlock)]
    [InlineData(LinesPerBlock + 1)]
    [InlineData(LinesPerBlock * 2)]
    [InlineData(LinesPerBlock * 3 + 7)]
    public async Task Counts_and_addresses_lines_across_block_boundaries(int lineCount)
    {
        var text = new StringBuilder();

        for (var i = 0; i < lineCount; i++)
        {
            text.Append("line ").Append(i).Append('\n');
        }

        using var file = TempTextFile.FromText(text.ToString());
        using var document = await OpenAsync(file);

        Assert.Equal(lineCount, document.LineCount);

        // The boundaries themselves and both sides of them.
        foreach (var index in new[] { 0, LinesPerBlock - 1, LinesPerBlock, lineCount - 1 })
        {
            if (index < lineCount)
            {
                Assert.Equal($"line {index}", document.GetLine(index));
            }
        }
    }

    [Fact]
    public async Task A_sweep_from_the_middle_of_a_block_starts_at_the_right_line()
    {
        var text = new StringBuilder();

        for (var i = 0; i < LinesPerBlock * 2; i++)
        {
            text.Append("line ").Append(i).Append('\n');
        }

        using var file = TempTextFile.FromText(text.ToString());
        using var document = await OpenAsync(file);

        var start = LinesPerBlock + 100;
        var swept = document.ReadFrom(start).Take(3).ToArray();

        Assert.Equal(start, swept[0].Index);
        Assert.Equal($"line {start}", swept[0].Text);
        Assert.Equal($"line {start + 2}", swept[2].Text);
    }

    [Fact]
    public async Task A_line_too_long_to_show_is_marked_but_swept_whole()
    {
        // Minified HTML is the real case: one line, longer than any display cut.
        const int length = 40_000;

        using var file = TempTextFile.FromText(new string('a', length) + "\ntail\n");
        using var document = await OpenAsync(file);

        var shown = document.GetLine(0);
        var swept = Sweep(document);

        Assert.EndsWith("…", shown);
        Assert.True(
            shown.Length < length,
            "the displayed line should be cut, otherwise there is nothing to mark");

        Assert.Equal(length, swept[0].Length);
        Assert.Equal(new string('a', length), swept[0]);
        Assert.Equal("tail", swept[1]);
    }

    [Fact]
    public async Task Indexing_reports_progress_and_finishes_at_one()
    {
        var text = new StringBuilder();

        for (var i = 0; i < 5_000; i++)
        {
            text.Append("line ").Append(i).Append('\n');
        }

        using var file = TempTextFile.FromText(text.ToString());

        var reports = new List<double>();
        var progress = new Progress<double>(reports.Add);

        using var document = await FileTextDocument.OpenAsync(file.Path, progress);

        // Progress is posted to the captured context; on a test thread it runs on
        // the thread pool, so give the posts a moment to arrive.
        await Task.Delay(50);

        Assert.NotEmpty(reports);
        Assert.Equal(1, reports[^1]);
    }

    [Fact]
    public async Task Indexing_can_be_cancelled()
    {
        var text = new StringBuilder();

        for (var i = 0; i < 50_000; i++)
        {
            text.Append("line ").Append(i).Append('\n');
        }

        using var file = TempTextFile.FromText(text.ToString());
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileTextDocument.OpenAsync(file.Path, null, cts.Token));
    }

    [Fact]
    public async Task Reading_a_disposed_document_is_refused()
    {
        using var file = TempTextFile.FromText("alpha\n");
        var document = await OpenAsync(file);

        document.Dispose();

        Assert.Throws<ObjectDisposedException>(() => document.GetLine(0));
    }
}
