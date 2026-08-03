namespace Warhorse.Models;

/// <summary>
/// A source the reader can load text from (file, URL, generator).
/// </summary>
public interface ITextSource
{
    /// <summary>Short display name shown in the reader header.</summary>
    string Name { get; }

    /// <summary>Loads the whole text of the source.</summary>
    Task<string> LoadAsync(CancellationToken cancellationToken = default);
}
