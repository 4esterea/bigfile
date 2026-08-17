using System.IO;
using System.Text;

namespace Bigfile.Tests;

/// <summary>
/// A file in the temp directory that removes itself with the test, so the
/// document tests can work on real bytes rather than on a stand-in for a file.
/// </summary>
internal sealed class TempTextFile : IDisposable
{
    private TempTextFile(string path) => Path = path;

    public string Path { get; }

    public static TempTextFile FromBytes(params byte[] bytes)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"Bigfile-test-{Guid.NewGuid():N}.txt");

        File.WriteAllBytes(path, bytes);
        return new TempTextFile(path);
    }

    /// <summary>Writes the text as UTF-8, with no byte order mark and no additions.</summary>
    public static TempTextFile FromText(string text) =>
        FromBytes(new UTF8Encoding(false).GetBytes(text));

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
    }
}
