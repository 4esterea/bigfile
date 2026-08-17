using System.IO;
using System.Text;

namespace Bigfile.Models;

/// <summary>
/// Writes a document — or just the lines a filter picked out — to a text file.
///
/// The text is streamed line by line straight from the source, so saving a
/// 50 GB document costs no more memory than saving a small one.
/// </summary>
public static class TextExport
{
    private const int WriteBufferSize = 1 << 20;

    /// <param name="lines">
    /// Ascending document line indexes to write, or null for the whole text.
    /// </param>
    public static void Save(
        ITextDocument document,
        string path,
        IReadOnlyList<int>? lines = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // FileShare.None makes saving onto the file the reader currently has
        // open fail cleanly, instead of truncating the text it is reading.
        using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, WriteBufferSize);

        using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        if (lines is null)
        {
            WriteAll(document, writer, progress, cancellationToken);
        }
        else
        {
            WriteSubset(document, writer, lines, progress, cancellationToken);
        }

        progress?.Report(1);
    }

    private static void WriteAll(
        ITextDocument document,
        TextWriter writer,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var total = Math.Max(document.LineCount, 1);
        var interval = ProgressPacing.Interval(total);

        foreach (var (index, text) in document.ReadFrom(0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteLine(text);

            if (progress is not null && index % interval == 0)
            {
                progress.Report((double)index / total);
            }
        }
    }

    /// <summary>
    /// Writes the selected lines in a single forward sweep. The filter collects
    /// them in ascending order, so no seeking back is ever needed.
    /// </summary>
    private static void WriteSubset(
        ITextDocument document,
        TextWriter writer,
        IReadOnlyList<int> lines,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var next = 0;
        var interval = ProgressPacing.Interval(lines.Count);

        foreach (var (index, text) in document.ReadFrom(lines[0]))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (index != lines[next])
            {
                continue;
            }

            writer.WriteLine(text);
            next++;

            if (next == lines.Count)
            {
                break;
            }

            if (progress is not null && next % interval == 0)
            {
                progress.Report((double)next / lines.Count);
            }
        }
    }
}
