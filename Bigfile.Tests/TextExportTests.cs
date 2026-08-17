using System.IO;
using Bigfile.Models;

namespace Bigfile.Tests;

/// <summary>
/// Saving has to reproduce what the reader was showing — including the parts of a
/// long line the display had to cut.
/// </summary>
public class TextExportTests
{
    private static string OutputPath() =>
        Path.Combine(Path.GetTempPath(), $"Bigfile-out-{Guid.NewGuid():N}.txt");

    [Fact]
    public async Task Writes_the_whole_document()
    {
        using var file = TempTextFile.FromText("alpha\nbeta\ngamma\n");
        using var document = await FileTextDocument.OpenAsync(file.Path);

        var output = OutputPath();

        try
        {
            TextExport.Save(document, output);

            Assert.Equal(["alpha", "beta", "gamma"], File.ReadAllLines(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Writes_only_the_lines_a_filter_picked()
    {
        using var file = TempTextFile.FromText("alpha\nbeta\ngamma\ndelta\n");
        using var document = await FileTextDocument.OpenAsync(file.Path);

        var output = OutputPath();

        try
        {
            TextExport.Save(document, output, [1, 3]);

            Assert.Equal(["beta", "delta"], File.ReadAllLines(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Saves_a_long_line_in_full()
    {
        // The display cuts a line this long; the saved file must not.
        const int length = 40_000;

        using var file = TempTextFile.FromText(new string('a', length) + "\ntail\n");
        using var document = await FileTextDocument.OpenAsync(file.Path);

        var output = OutputPath();

        try
        {
            TextExport.Save(document, output);

            var saved = File.ReadAllLines(output);

            Assert.Equal(length, saved[0].Length);
            Assert.Equal("tail", saved[1]);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Saving_can_be_cancelled()
    {
        using var file = TempTextFile.FromText("alpha\nbeta\n");
        using var document = await FileTextDocument.OpenAsync(file.Path);
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        var output = OutputPath();

        try
        {
            Assert.ThrowsAny<OperationCanceledException>(
                () => TextExport.Save(document, output, null, null, cts.Token));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Saving_onto_the_open_file_fails_instead_of_truncating_it()
    {
        using var file = TempTextFile.FromText("alpha\nbeta\n");
        using var document = await FileTextDocument.OpenAsync(file.Path);

        Assert.ThrowsAny<IOException>(() => TextExport.Save(document, file.Path));

        // The source is still readable, which is the point of the exclusive open.
        Assert.Equal("alpha", document.GetLine(0));
    }
}
