using System.IO;
using System.Text;

namespace Warhorse.Models;

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
/// scan looks for the 0x0A byte. UTF-16 files are rejected while opening.
/// </summary>
public sealed class FileTextDocument : ITextDocument
{
    /// <summary>Lines per indexed block. Trades index size for read cost.</summary>
    private const int LinesPerBlock = 512;

    /// <summary>Blocks kept decoded in memory (~64 * 512 lines).</summary>
    private const int MaxCachedBlocks = 64;

    /// <summary>A single line is truncated past this many bytes.</summary>
    private const int MaxLineBytes = 8192;

    private const int ScanBufferSize = 1 << 20;
    private const int ReadBufferSize = 1 << 16;

    private readonly Dictionary<int, string[]> _cache = new();
    private readonly LinkedList<int> _cacheOrder = new();

    private readonly FileStream _stream;
    private readonly long[] _blockOffsets;
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private readonly byte[] _lineBuffer = new byte[MaxLineBytes];
    private readonly string? _deleteOnDispose;

    private bool _disposed;

    private FileTextDocument(
        FileStream stream,
        long[] blockOffsets,
        int lineCount,
        string? deleteOnDispose)
    {
        _stream = stream;
        _blockOffsets = blockOffsets;
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
        var bomLength = ReadBomLength(path);
        var (offsets, lineCount) = BuildIndex(path, bomLength, progress, cancellationToken);

        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            ReadBufferSize, FileOptions.RandomAccess);

        return new FileTextDocument(
            stream, offsets, lineCount,
            deleteOnDispose ? path : null);
    }

    /// <summary>
    /// Returns the length of the byte order mark to skip, rejecting encodings
    /// the byte-wise newline scan cannot handle.
    /// </summary>
    private static int ReadBomLength(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> bom = stackalloc byte[4];
        var read = stream.Read(bom);

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
        {
            return 3;
        }

        if (read >= 2 && ((bom[0] == 0xFF && bom[1] == 0xFE) || (bom[0] == 0xFE && bom[1] == 0xFF)))
        {
            throw new NotSupportedException(
                "UTF-16 files are not supported. Please convert the file to UTF-8.");
        }

        return 0;
    }

    /// <summary>
    /// Streams through the file counting newlines and recording the byte offset
    /// of every LinesPerBlock-th line.
    /// </summary>
    private static (long[] Offsets, int LineCount) BuildIndex(
        string path,
        int bomLength,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            ScanBufferSize, FileOptions.SequentialScan);

        var length = stream.Length;
        stream.Position = bomLength;

        var offsets = new List<long> { bomLength };
        var buffer = new byte[ScanBufferSize];
        long position = bomLength;
        long lineCount = 0;
        var pendingLine = false;
        var lastReport = 0L;

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    pendingLine = true;
                    continue;
                }

                lineCount++;
                pendingLine = false;

                if (lineCount % LinesPerBlock == 0)
                {
                    offsets.Add(position + i + 1);
                }
            }

            position += read;

            // Report at most once per scanned megabyte.
            if (progress is not null && position - lastReport >= ScanBufferSize)
            {
                lastReport = position;
                progress.Report(length == 0 ? 1 : (double)position / length);
            }
        }

        // Text after the final newline forms one more line.
        if (pendingLine)
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
                    lines[produced++] = Decode(lineLength);
                    break;
                }
            }

            var b = _readBuffer[bufferPos++];

            if (b == (byte)'\n')
            {
                lines[produced++] = Decode(lineLength);
                lineLength = 0;
            }
            else if (lineLength < MaxLineBytes)
            {
                _lineBuffer[lineLength++] = b;
            }
        }

        // Defensive: a file changed under us could yield fewer lines.
        for (; produced < count; produced++)
        {
            lines[produced] = string.Empty;
        }

        return lines;
    }

    private string Decode(int length)
    {
        // Drop the CR of a CRLF pair.
        if (length > 0 && _lineBuffer[length - 1] == (byte)'\r')
        {
            length--;
        }

        return length == 0 ? string.Empty : Encoding.UTF8.GetString(_lineBuffer, 0, length);
    }

    private void CacheBlock(int block, string[] lines)
    {
        _cache[block] = lines;
        _cacheOrder.AddLast(block);

        while (_cacheOrder.Count > MaxCachedBlocks)
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
