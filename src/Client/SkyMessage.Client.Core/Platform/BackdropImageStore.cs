namespace SkyMessage.Client.Core.Platform;

/// <summary>
/// The user's own background pictures on this device (the app's background, the chats'): device settings like the
/// appearance settings that describe them, never part of the offline cache, so clearing it or signing out keeps them.
/// MAUI: files in the app's private data dir (backdrops/), served to the page at "/_sm/backdrops/{file}"; browser: the
/// IndexedDB database "skymessage-backdrops" (object store "files", key = file name, value = { type, blob }), which
/// boot.js reads itself to show a picture before Blazor starts. Implementations never throw for a missing file.
/// </summary>
public interface IBackdropImageStore
{
    bool IsSupported { get; }

    Task SaveAsync(string fileName, byte[] content, string contentType, CancellationToken cancellationToken = default);

    Task DeleteAsync(string fileName);

    /// <summary>File names of every stored picture.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Where the page loads a picture from (boot.js understands both): an app-origin path the native host serves
    /// ("/_sm/backdrops/abc.jpg"), or "idb:{file name}" for the browser's IndexedDB database.
    /// </summary>
    string PageSource(string fileName);
}
