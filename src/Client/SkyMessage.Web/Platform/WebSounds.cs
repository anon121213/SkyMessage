using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.Web.Platform;

/// <summary>
/// The user's own sounds in an IndexedDB database of their own (web-platform.js <c>skyPlatform.sounds</c>): they are
/// device settings, so neither signing out nor clearing the offline cache removes them. Picked with a file input
/// filtered to audio.
/// </summary>
public sealed class WebCustomSoundStore(WebBridge bridge, WebBlobs blobs, ILogger<WebCustomSoundStore> logger) : ICustomSoundStore
{
    public bool IsSupported => true;

    public async Task<PickedFile?> PickAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var picked = await bridge.Js.InvokeAsync<PickedBlob[]>("skyPlatform.files.pick", cancellationToken, CustomSoundRules.AcceptAttribute, false, null)
                .ConfigureAwait(false);
            return picked.Length == 0 ? null : blobs.ToPickedFile(picked[0].Ref, picked[0].Name, picked[0].Type, picked[0].Size);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Audio file picker failed");
            return null;
        }
    }

    public async Task SaveAsync(CustomSound sound, byte[] content, CancellationToken cancellationToken = default) =>
        await bridge.Js.InvokeVoidAsync("skyPlatform.sounds.put", cancellationToken, sound.FileName, content, sound.ContentType).ConfigureAwait(false);

    public async Task<byte[]?> ReadAsync(CustomSound sound, CancellationToken cancellationToken = default)
    {
        try
        {
            return await bridge.Js.InvokeAsync<byte[]?>("skyPlatform.sounds.get", cancellationToken, sound.FileName).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Could not read a custom sound");
            return null;
        }
    }

    /// <summary>A browser has no system player of its own: notifications here play no custom file.</summary>
    public Task<bool> IsSystemPlayableAsync(byte[] content, string extension, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public async Task DeleteAsync(CustomSound sound)
    {
        try
        {
            await bridge.Js.InvokeVoidAsync("skyPlatform.sounds.del", sound.FileName).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            logger.LogDebug(ex, "Could not delete a custom sound");
        }
    }

    private sealed record PickedBlob(string Ref, string Name, string Type, long Size);
}
