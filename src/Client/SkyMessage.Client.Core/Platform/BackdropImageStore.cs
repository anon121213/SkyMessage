namespace SkyMessage.Client.Core.Platform;

/// <summary>A stored picture: its file name and when it was stored (null when the store cannot tell).</summary>
public sealed record StoredBackdropFile(string FileName, DateTimeOffset? SavedAt);

/// <summary>
/// The user's own background pictures on this device (the app's background, the chats'): device settings like the
/// appearance settings that describe them, never part of the offline cache, so clearing it or signing out keeps them.
/// MAUI: files in the app's private data dir (backdrops/), served to the page at "/_sm/backdrops/{file}"; browser: the
/// IndexedDB database "skymessage-backdrops" (object store "files", key = file name, value = { type, blob, savedAt }),
/// which boot.js reads itself to show a picture before Blazor starts. Implementations never throw for a missing file.
/// </summary>
public interface IBackdropImageStore
{
    bool IsSupported { get; }

    Task SaveAsync(string fileName, byte[] content, string contentType, CancellationToken cancellationToken = default);

    Task DeleteAsync(string fileName);

    /// <summary>Every stored picture, with when it was stored (the file's time on a device, the record's in a browser).</summary>
    Task<IReadOnlyList<StoredBackdropFile>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Where the page loads a picture from (boot.js understands both): an app-origin path the native host serves
    /// ("/_sm/backdrops/abc.jpg"), or "idb:{file name}" for the browser's IndexedDB database.
    /// </summary>
    string PageSource(string fileName);
}
