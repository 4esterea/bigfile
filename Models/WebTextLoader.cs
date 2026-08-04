using System.IO;
using System.Net.Http;

namespace Warhorse.Models;

/// <summary>
/// Downloads a URL into a temporary file and opens it as a document, so the
/// response never sits in memory. The temporary file is removed on close.
/// </summary>
public static class WebTextLoader
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private const int CopyBufferSize = 1 << 20;

    /// <summary>
    /// Parses user input into an absolute http/https URL,
    /// assuming https when no scheme is typed.
    /// </summary>
    public static bool TryParse(string? input, out Uri uri)
    {
        uri = null!;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out uri!)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>Name shown in the reader header for a downloaded document.</summary>
    public static string NameOf(Uri uri) => uri.Host + uri.AbsolutePath;

    public static async Task<ITextDocument> OpenAsync(
        Uri uri,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"warhorse-{Guid.NewGuid():N}.tmp");

        try
        {
            // The download fills the first half of the progress bar, the scan the second.
            await DownloadAsync(uri, tempPath, Scale(progress, 0, 0.5), cancellationToken);
            return await FileTextDocument.OpenAsync(
                tempPath, Scale(progress, 0.5, 1), cancellationToken, deleteOnDispose: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static async Task DownloadAsync(
        Uri uri,
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            CopyBufferSize, useAsync: true);

        var buffer = new byte[CopyBufferSize];
        long copied = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;

            // Without Content-Length there is nothing to compare against.
            if (progress is not null && total is > 0)
            {
                progress.Report((double)copied / total.Value);
            }
        }

        progress?.Report(1);
    }

    /// <summary>Maps a 0..1 progress report onto the given sub-range.</summary>
    private static IProgress<double>? Scale(IProgress<double>? progress, double from, double to)
    {
        return progress is null
            ? null
            : new Progress<double>(value => progress.Report(from + value * (to - from)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Nothing useful to do if the temp file cannot be removed.
        }
    }
}
