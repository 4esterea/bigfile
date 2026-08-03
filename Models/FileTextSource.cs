using System.IO;
using System.Text;

namespace Warhorse.Models;

/// <summary>
/// Text source backed by a file on disk.
/// </summary>
public sealed class FileTextSource : ITextSource
{
    private readonly string _path;

    public FileTextSource(string path)
    {
        _path = path;
    }

    public string Name => Path.GetFileName(_path);

    public string FullPath => _path;

    public async Task<string> LoadAsync(CancellationToken cancellationToken = default)
    {
        // Detect the encoding from the BOM, fall back to UTF-8.
        using var reader = new StreamReader(_path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
