using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.Web.Platform;

/// <summary>
/// The user's background pictures in an IndexedDB database of their own (web-platform.js <c>skyPlatform.backdrops</c>,
/// records { type, blob, savedAt }): device settings, so neither signing out nor clearing the offline cache removes them. The page reads them from there
/// itself (boot.js, "idb:" sources), before Blazor starts too.
/// </summary>
public sealed class WebBackdropImageStore(WebBridge bridge, ILogger<WebBackdropImageStore> logger) : IBackdropImageStore
{
    public bool IsSupported => true;

    public async Task SaveAsync(string fileName, byte[] content, string contentType, CancellationToken cancellationToken = default) =>
        await bridge.Js.InvokeVoidAsync("skyPlatform.backdrops.put", cancellationToken, fileName, content, contentType).ConfigureAwait(false);

    public async Task DeleteAsync(string fileName)
    {
        try
        {
            await bridge.Js.InvokeVoidAsync("skyPlatform.backdrops.del", fileName).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            logger.LogDebug(ex, "Could not delete a background picture");
        }
    }

    public async Task<IReadOnlyList<StoredBackdropFile>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var entries = await bridge.Js.InvokeAsync<Entry[]>("skyPlatform.backdrops.list", cancellationToken).ConfigureAwait(false);
            return entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Select(e => new StoredBackdropFile(e.Name!, e.SavedAt is > 0 and var ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null))
                .ToList();
        }
        catch (JSException ex)
        {
            logger.LogDebug(ex, "Could not list the background pictures");
            return [];
        }
    }

    /// <summary>A record of web-platform.js's list: the key and the record's savedAt (ms since 1970; absent on records stored before it).</summary>
    private sealed record Entry(string? Name, double? SavedAt);

    public string PageSource(string fileName) => "idb:" + fileName;
}
