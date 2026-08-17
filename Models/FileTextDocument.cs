using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Unicode;

namespace Bigfile.Models;

/// <summary>
/// A text file kept on disk and read line by line on demand.
///
/// The file is scanned once to build a sparse index: the byte offset of every
/// <see cref="LinesPerBlock"/>-th line. For a 50 GB file with ~500M lines that
/// index costs about 8 MB, while the text itself never enters memory.
/// A line is fetched by seeking to its block and reading that block only;
/// recently used blocks are cached so sequential scrolling stays cheap.
///
/// Only byte-oriented encodings (UTF-8, ASCII, ANSI) are supported, because the
/// scan looks for a single separator byte. Which encoding and which separator is
/// decided from a sample of the head of the file, so a Windows-codepage file
/// reads as its own text instead of as mojibake, and a file with classic Mac
/// line endings reads as lines instead of as one enormous one. UTF-16 files are
/// rejected while opening rather than being misread.
///
/// A line longer than a screenful is cut for display and marked with an ellipsis,
/// but a sweep — which is what search and save read through — keeps the line
/// whole, so neither silently drops the tail of a long line. Minified HTML, where
/// the whole document can be a single line, depends on that.
/// </summary>
public sealed class FileTextDocument : ITextDocument
{
    /// <summary>Lines per indexed block. Trades index size for read cost.</summary>
    private const int LinesPerBlock = 512;

    /// <summary>Blocks kept decoded in memory (~64 * 512 lines).</summary>
    private const int MaxCachedBlocks = 64;

    /// <summary>
    /// Bytes of a line kept for display. Far more than a window can show, and
    /// the cut is marked, so nothing disappears unannounced.
    /// </summary>
    private const int MaxDisplayLineBytes = 1 << 13;

    /// <summary>
    /// Hard ceiling for a line read by a sweep. The buffer grows into it as
    /// needed, so ordinary files never pay for it, and a single-line document is
    /// still searched and saved whole.
    /// </summary>
    private const int MaxSweepLineBytes = 1 << 26;

    /// <summary>Buffer a sweep starts with before growing.</summary>
    private const int InitialSweepLineBytes = 1 << 12;

    /// <summary>Appended to a display line whose tail was cut off.</summary>
    private const string TruncationMarker = " …";

    private const int ScanBufferSize = 1 << 20;

    /// <summary>
    /// Granularity at which the scan decides between counting newlines and
    /// locating them. Small enough that most chunks hold no block boundary,
    /// large enough to keep the vectorised count worthwhile.
    /// </summary>
    private const int ScanChunkSize = 8192;

    private const int ReadBufferSize = 1 << 16;

    private readonly Dictionary<int, string[]> _cache = new();
    private readonly LinkedList<int> _cacheOrder = new();

    private readonly FileStream _stream;
    private readonly string _path;
    private readonly long[] _blockOffsets;
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private readonly byte[] _lineBuffer = new byte[MaxDisplayLineBytes];
    private readonly string? _deleteOnDispose;

    private bool _disposed;

    private readonly TextFormat _format;

    private FileTextDocument(
        FileStream stream,
        string path,
        long[] blockOffsets,
        int lineCount,
        TextFormat format,
        string? deleteOnDispose)
    {
        _stream = stream;
        _path = path;
        _blockOffsets = blockOffsets;
        _format = format;
        _deleteOnDispose = deleteOnDispose;
        LineCount = lineCount;
    }

    public int LineCount { get; }

    /// <summary>
    /// Scans the file and opens it for random access.
    /// </summary>
    /// <param name="path">File to open.</param>
    /// <param name="deleteOnDispose">
    /// Set for temporary files (e.g. a download) so they are removed on close.
    /// </param>
    public static Task<ITextDocument> OpenAsync(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        bool deleteOnDispose = false)
    {
        return Task.Run<ITextDocument>(
            () => Open(path, progress, cancellationToken, deleteOnDispose),
            cancellationToken);
    }

    private static FileTextDocument Open(
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        bool deleteOnDispose)
    {
        var format = DetectFormat(path);
        var (offsets, lineCount) = BuildIndex(path, format, progress, cancellationToken);

        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            ReadBufferSize, FileOptions.RandomAccess);

        return new FileTextDocument(
            stream, path, offsets, lineCount, format,
            deleteOnDispose ? path : null);
    }

    /// <summary>
    /// How a file's bytes become lines: how many bytes of byte order mark to
    /// skip, which byte separates lines, and how the rest decodes.
    /// </summary>
    private readonly record struct TextFormat(int BomLength, byte Newline, Encoding Encoding);

    /// <summary>Bytes of the head of the file the format is guessed from.</summary>
    private const int ProbeBytes = 1 << 16;

    /// <summary>
    /// The system's ANSI code page, used for files that are not valid UTF-8. The
    /// provider has to be registered because .NET ships only the Unicode
    /// encodings by default; where that is unavailable, Latin-1 at least maps
    /// every byte to a character instead of to a replacement mark.
    /// </summary>
    private static readonly Encoding AnsiEncoding = ResolveAnsiEncoding();

    private static Encoding ResolveAnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PlatformNotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>
    /// Decides the format from a sample of the head of the file, and rejects
    /// encodings the byte-wise scan cannot handle.
    /// </summary>
    private static TextFormat DetectFormat(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var buffer = new byte[ProbeBytes];
        var read = stream.Read(buffer, 0, buffer.Length);
        var sample = buffer.AsSpan(0, read);

        if (sample.Length >= 2 &&
            ((sample[0] == 0xFF && sample[1] == 0xFE) || (sample[0] == 0xFE && sample[1] == 0xFF)))
        {
            throw new NotSupportedException(
                "UTF-16 files are not supported. Please convert the file to UTF-8.");
        }

        var bomLength = sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF
            ? 3
            : 0;

        var text = sample[bomLength..];

        // A file with no LF anywhere in its head but with CRs is the classic Mac
        // layout, where CR alone ends a line. Anything else — including a file
        // with no separator at all — is treated as LF-separated.
        var newline = !text.Contains((byte)'\n') && text.Contains((byte)'\r')
            ? (byte)'\r'
            : (byte)'\n';

        // A byte order mark settles the question; otherwise the sample decides,
        // and only text that really is valid UTF-8 is decoded as UTF-8.
        var encoding = bomLength > 0 || IsUtf8(text) ? Encoding.UTF8 : AnsiEncoding;

        return new TextFormat(bomLength, newline, encoding);
    }

    /// <summary>
    /// Whether a sample decodes as UTF-8. The tail is trimmed of a possibly
    /// half-read multi-byte sequence first, so cutting the sample mid-character
    /// does not condemn a whole valid file.
    /// </summary>
    private static bool IsUtf8(ReadOnlySpan<byte> sample)
    {
        var length = sample.Length;

        // A sequence is at most four bytes, so at most three can be pending.
        for (var i = 0; i < 3 && length > 0 && sample[length - 1] >= 0x80; i++)
        {
            length--;
        }

        return Utf8.IsValid(sample[..length]);
    }

    /// <summary>
    /// Streams through the file counting newlines and recording the byte offset
    /// of every LinesPerBlock-th line.
    ///
    /// Both <see cref="MemoryExtensions.Count{T}(ReadOnlySpan{T}, T)"/> and
    /// <see cref="MemoryExtensions.IndexOf{T}(ReadOnlySpan{T}, T)"/> are
    /// SIMD-accelerated, so the file is scanned a vector at a time. Only the
    /// chunks that actually contain a block boundary are walked newline by
    /// newline; the rest are merely counted, which is roughly three times
    /// faster than inspecting every byte.
    /// </summary>
    private static (long[] Offsets, int LineCount) BuildIndex(
        string path,
        TextFormat format,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            ScanBufferSize, FileOptions.SequentialScan);

        var newline = format.Newline;
        var length = stream.Length;
        stream.Position = format.BomLength;

        var offsets = new List<long> { format.BomLength };
        var buffer = new byte[ScanBufferSize];
        long position = format.BomLength;
        long lineCount = 0;
        var lastByte = newline;
        var lastReport = 0L;

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var span = buffer.AsSpan(0, read);

            for (var chunkStart = 0; chunkStart < read; chunkStart += ScanChunkSize)
            {
                var chunk = span.Slice(
                    chunkStart, Math.Min(ScanChunkSize, read - chunkStart));

                var count = chunk.Count(newline);
                var nextBoundary = (lineCount / LinesPerBlock + 1) * LinesPerBlock;

                if (lineCount + count < nextBoundary)
                {
                    // No block starts inside this chunk, so no offset is needed.
                    lineCount += count;
                    continue;
                }

                var scanned = 0;

                while (true)
                {
                    var next = chunk[scanned..].IndexOf(newline);

                    if (next < 0)
                    {
                        break;
                    }

                    scanned += next + 1;
                    lineCount++;

                    if (lineCount % LinesPerBlock == 0)
                    {
                        offsets.Add(position + chunkStart + scanned);
                    }
                }
            }

            lastByte = span[^1];
            position += read;

            // Report at most once per scanned megabyte.
            if (progress is not null && position - lastReport >= ScanBufferSize)
            {
                lastReport = position;
                progress.Report(length == 0 ? 1 : (double)position / length);
            }
        }

        // Text after the final newline forms one more line.
        if (lastByte != newline)
        {
            lineCount++;
        }

        if (lineCount > int.MaxValue)
        {
            throw new NotSupportedException(
                $"The file has more than {int.MaxValue:N0} lines, which the viewer cannot address.");
        }

        // The last recorded offset can point at EOF when the file ends exactly
        // on a block boundary; that block holds no lines.
        var blockCount = (int)((lineCount + LinesPerBlock - 1) / LinesPerBlock);
        if (offsets.Count > blockCount)
        {
            offsets.RemoveRange(blockCount, offsets.Count - blockCount);
        }

        progress?.Report(1);
        return (offsets.ToArray(), (int)lineCount);
    }

    public string GetLine(int index)
    {
        if (index < 0 || index >= LineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        var block = index / LinesPerBlock;

        if (!_cache.TryGetValue(block, out var lines))
        {
            lines = ReadBlock(block);
            CacheBlock(block, lines);
        }
        else
        {
            _cacheOrder.Remove(block);
            _cacheOrder.AddLast(block);
        }

        return lines[index - block * LinesPerBlock];
    }

    /// <summary>Reads one block of lines starting at its indexed offset.</summary>
    private string[] ReadBlock(int block)
    {
        var count = Math.Min(LinesPerBlock, LineCount - block * LinesPerBlock);
        var lines = new string[count];

        _stream.Position = _blockOffsets[block];

        var produced = 0;
        var lineLength = 0;
        var truncated = false;
        var bufferPos = 0;
        var buffered = 0;

        while (produced < count)
        {
            if (bufferPos == buffered)
            {
                buffered = _stream.Read(_readBuffer, 0, _readBuffer.Length);
                bufferPos = 0;

                if (buffered == 0)
                {
                    // End of file: whatever is buffered is the final line.
                    lines[produced++] = Decode(lineLength, truncated);
                    break;
                }
            }

            var b = _readBuffer[bufferPos++];

            if (b == _format.Newline)
            {
                lines[produced++] = Decode(lineLength, truncated);
                lineLength = 0;
                truncated = false;
            }
            else if (lineLength < MaxDisplayLineBytes)
            {
                _lineBuffer[lineLength++] = b;
            }
            else
            {
                // Past what is worth rendering; the marker says so on screen.
                truncated = true;
            }
        }

        return lines;
    }

    private string Decode(int length, bool truncated) =>
        truncated
            ? Decode(_lineBuffer, length) + TruncationMarker
            : Decode(_lineBuffer, length);

    private string Decode(byte[] buffer, int length)
    {
        // Drop the CR of a CRLF pair — but not when CR is itself the separator,
        // where a trailing CR cannot be part of the line to begin with.
        if (_format.Newline == (byte)'\n' &&
            length > 0 &&
            buffer[length - 1] == (byte)'\r')
        {
            length--;
        }

        return length == 0 ? string.Empty : _format.Encoding.GetString(buffer, 0, length);
    }

    /// <summary>
    /// Sweeps the file from a line onwards. The enumeration opens its own
    /// stream and keeps its buffers in locals, so it never touches the state
    /// <see cref="GetLine"/> uses and a search can run while the UI renders.
    ///
    /// Lines come back whole — the buffer grows up to
    /// <see cref="MaxSweepLineBytes"/> — because a sweep is what search and save
    /// read through, and both would be wrong on a cut line.
    /// </summary>
    public IEnumerable<(int Index, string Text)> ReadFrom(int startLine)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(startLine);

        if (startLine >= LineCount)
        {
            yield break;
        }

        using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            ScanBufferSize, FileOptions.SequentialScan);

        var readBuffer = new byte[ScanBufferSize];
        var lineBuffer = new byte[InitialSweepLineBytes];
        var bufferPos = 0;
        var buffered = 0;

        // Offsets are only known at block boundaries, so start at the block
        // holding the line and read the few lines before it away.
        var block = startLine / LinesPerBlock;
        stream.Position = _blockOffsets[block];

        for (var index = block * LinesPerBlock; index < LineCount; index++)
        {
            var length = 0;
            var endOfFile = false;

            while (true)
            {
                if (bufferPos == buffered)
                {
                    buffered = stream.Read(readBuffer, 0, readBuffer.Length);
                    bufferPos = 0;

                    if (buffered == 0)
                    {
                        // Whatever is buffered is the last line of the file.
                        endOfFile = true;
                        break;
                    }
                }

                var b = readBuffer[bufferPos++];

                if (b == _format.Newline)
                {
                    break;
                }

                if (length == lineBuffer.Length && length < MaxSweepLineBytes)
                {
                    Array.Resize(
                        ref lineBuffer,
                        Math.Min(lineBuffer.Length * 2, MaxSweepLineBytes));
                }

                if (length < lineBuffer.Length)
                {
                    lineBuffer[length++] = b;
                }
            }

            if (index >= startLine)
            {
                yield return (index, Decode(lineBuffer, length));
            }

            if (endOfFile)
            {
                yield break;
            }
        }
    }

    private void CacheBlock(int block, string[] lines)
    {
        _cache[block] = lines;
        _cacheOrder.AddLast(block);

        if (_cacheOrder.Count > MaxCachedBlocks)
        {
            var oldest = _cacheOrder.First!.Value;
            _cacheOrder.RemoveFirst();
            _cache.Remove(oldest);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Clear();
        _cacheOrder.Clear();
        _stream.Dispose();

        if (_deleteOnDispose is not null)
        {
            try
            {
                File.Delete(_deleteOnDispose);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing the close over.
            }
        }
    }
}
