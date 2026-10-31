using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Services;

/// <summary>
/// Durable copies of outbox attachments in an app-private folder (outbox/{itemKey}/), so queued messages
/// survive the app being killed. On Android, a gallery item picked through the Photo Picker whose
/// persistable read grant we hold is stored by reference instead of being copied (no duplicate of a large
/// video); if the original disappears before sending, <see cref="TryOpenAsync"/> returns null.
/// </summary>
public sealed class OutboxFileStore(ILogger<OutboxFileStore> logger) : IOutboxFileStore
{
    private const string MetaFile = ".meta.json";

    public bool IsSupported => true;

    public async Task<PickedFile> PersistAsync(Guid itemKey, PickedFile source, CancellationToken cancellationToken = default)
    {
        var dir = ItemDir(itemKey);
        var fileName = FileNames.Sanitize(source.FileName);

        return await Task.Run(async () =>
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

#if ANDROID
            if (SkyMessage.App.Droid.ContentUriHelper.HasPersistedReadGrant(source.PlatformRef))
            {
                var byRef = new Meta(fileName, source.ContentType, source.Size, source.PlatformRef);
                await WriteMetaAsync(dir, byRef, cancellationToken).ConfigureAwait(false);
                return ToPickedFile(dir, byRef);
            }
#endif
            var path = Path.Combine(dir, fileName);
            var temp = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp");
            try
            {
                if (source.LocalPath is { } local && File.Exists(local))
                {
                    File.Copy(local, temp, overwrite: true);
                }
                else
                {
                    await using var input = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                    await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                }
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                TryDeleteDir(dir);
                throw;
            }

            var meta = new Meta(fileName, source.ContentType, new FileInfo(path).Length, null);
            await WriteMetaAsync(dir, meta, cancellationToken).ConfigureAwait(false);
            return ToPickedFile(dir, meta);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PickedFile?> TryOpenAsync(Guid itemKey)
    {
        var dir = ItemDir(itemKey);
        return await Task.Run(async () =>
        {
            var metaPath = Path.Combine(dir, MetaFile);
            if (!File.Exists(metaPath)) return null;
            try
            {
                await using var stream = File.OpenRead(metaPath);
                var meta = await JsonSerializer.DeserializeAsync<Meta>(stream).ConfigureAwait(false);
                if (meta is null) return null;
                if (meta.ContentUri is not null)
                {
#if ANDROID
                    return SkyMessage.App.Droid.ContentUriHelper.CanRead(meta.ContentUri) ? ToPickedFile(dir, meta) : null;
#else
                    return null;
#endif
                }
                return File.Exists(Path.Combine(dir, meta.FileName)) ? ToPickedFile(dir, meta) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.LogWarning(ex, "Outbox item {Key} is unreadable", itemKey);
                return null;
            }
        }).ConfigureAwait(false);
    }

    public Task DeleteAsync(Guid itemKey) => Task.Run(() => TryDeleteDir(ItemDir(itemKey)));

    public Task<long> GetSizeBytesAsync() => Task.Run(() =>
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(AppPaths.Outbox, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
        }
        return total;
    });

    private static string ItemDir(Guid itemKey) => Path.Combine(AppPaths.Outbox, itemKey.ToString("N"));

    private static PickedFile ToPickedFile(string dir, Meta meta)
    {
#if ANDROID
        if (meta.ContentUri is { } uri)
        {
            return new PickedFile
            {
                FileName = meta.FileName,
                ContentType = meta.ContentType,
                Size = meta.Size,
                PlatformRef = uri,
                OpenReadAsync = _ => Task.FromResult(SkyMessage.App.Droid.ContentUriHelper.OpenRead(uri)),
            };
        }
#endif
        var path = Path.Combine(dir, meta.FileName);
        return new PickedFile
        {
            FileName = meta.FileName,
            ContentType = meta.ContentType,
            Size = meta.Size,
            LocalPath = path,
            OpenReadAsync = _ => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)),
        };
    }

    private static async Task WriteMetaAsync(string dir, Meta meta, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(dir, $".{Guid.NewGuid():N}.meta.tmp");
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, meta, cancellationToken: cancellationToken).ConfigureAwait(false);
        File.Move(temp, Path.Combine(dir, MetaFile), overwrite: true);
    }

    private void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not delete outbox folder {Dir}", dir);
        }
    }

    private sealed record Meta(string FileName, string ContentType, long Size, string? ContentUri);
}
