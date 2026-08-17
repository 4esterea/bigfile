using System.IO;
using System.Net.Http;

namespace Bigfile.Models;

/// <summary>
/// Downloads a URL into a temporary file and opens it as a document, so the
/// response never sits in memory. The temporary file is removed on close.
/// </summary>
public static class WebTextLoader
{
    /// <summary>
    /// The client's own timeout covers reading the body as well, so a download
    /// large enough to be worth this viewer would fail on the clock rather than
    /// on anything being wrong. Length is bounded by cancellation instead — the
    /// overlay's Cancel button — and stalls by the per-read timeout below.
    /// </summary>
    private static readonly HttpClient Client = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>How long a single read may stall before the download gives up.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);

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
        var tempPath = Path.Combine(Path.GetTempPath(), $"Bigfile-{Guid.NewGuid():N}.tmp");

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
        using var headers = WithReadTimeout(cancellationToken);

        HttpResponseMessage response;

        try
        {
            response = await Client.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, headers.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException(
                $"The server did not answer within {ReadTimeout.TotalMinutes:N0} minutes.");
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            await CopyAsync(response, path, progress, cancellationToken);
        }
    }

    /// <summary>Streams the response body to disk, a megabyte at a time.</summary>
    private static async Task CopyAsync(
        HttpResponseMessage response,
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            CopyBufferSize, useAsync: true);

        var buffer = new byte[CopyBufferSize];
        long copied = 0;

        while (true)
        {
            // Each read gets its own deadline, so a server that stops sending is
            // given up on while a download that is merely long is not.
            using var reading = WithReadTimeout(cancellationToken);

            int read;

            try
            {
                read = await source.ReadAsync(buffer, reading.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException(
                    $"The download stalled for over {ReadTimeout.TotalMinutes:N0} minutes.");
            }

            if (read == 0)
            {
                break;
            }

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

    /// <summary>
    /// The caller's cancellation plus a deadline of its own, so a stalled
    /// transfer is distinguishable from one the user cancelled.
    /// </summary>
    private static CancellationTokenSource WithReadTimeout(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ReadTimeout);
        return cts;
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
