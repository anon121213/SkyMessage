using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Services;

/// <summary>
/// Downloaded media bytes in the cache dir: content as media/c/{id}/{original file name} (the name matters
/// to "open with" apps and iOS previews), thumbnails as media/t/{id}.jpg. Every read touches the file's
/// write time, which is the LRU clock used by <see cref="TrimAsync"/>.
/// </summary>
public sealed class MediaFileStore(ILogger<MediaFileStore> logger) : IMediaFileStore
{
    private static readonly TimeSpan RecentlyUsed = TimeSpan.FromSeconds(30);

    public bool IsSupported => true;

    private static string ContentRoot => EnsureDir(Path.Combine(AppPaths.Media, "c"));

    private static string ThumbRoot => EnsureDir(Path.Combine(AppPaths.Media, "t"));

    public Task<string?> GetLocalPathAsync(Guid mediaId, bool thumbnail = false) => Task.Run(() =>
    {
        var path = thumbnail ? ThumbPath(mediaId) : FindContent(mediaId);
        if (path is null || !File.Exists(path)) return null;
        Touch(path);
        return path;
    });

    public async Task<string> SaveAsync(Guid mediaId, bool thumbnail, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            string target;
            string dir;
            if (thumbnail)
            {
                target = ThumbPath(mediaId);
                dir = ThumbRoot;
            }
            else
            {
                dir = EnsureDir(Path.Combine(ContentRoot, mediaId.ToString("N")));
                target = Path.Combine(dir, FileNames.Sanitize(fileName, mediaId.ToString("N")));
            }

            var temp = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await content.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);

                if (!thumbnail)
                {
                    foreach (var existing in Directory.EnumerateFiles(dir))
                    {
                        var name = Path.GetFileName(existing);
                        if (!name.StartsWith('.') && !string.Equals(existing, target, StringComparison.Ordinal)) TryDelete(existing);
                    }
                }
                File.Move(temp, target, overwrite: true);
                return target;
            }
            finally
            {
                TryDelete(temp);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<long> GetSizeBytesAsync() => Task.Run(() => Entries().Sum(e => e.Size));

    public Task TrimAsync(long maxBytes) => Task.Run(() =>
    {
        var entries = Entries().OrderBy(e => e.LastUsed).ToList();
        var total = entries.Sum(e => e.Size);
        var now = DateTime.UtcNow;
        foreach (var entry in entries)
        {
            if (total <= maxBytes) break;
            if (now - entry.LastUsed < RecentlyUsed) continue;
            if (entry.IsDirectory) TryDeleteDir(entry.Path);
            else TryDelete(entry.Path);
            total -= entry.Size;
        }
    });

    public Task ClearAsync() => Task.Run(() =>
    {
        foreach (var entry in Entries())
        {
            if (entry.IsDirectory) TryDeleteDir(entry.Path);
            else TryDelete(entry.Path);
        }
    });

    private static string ThumbPath(Guid mediaId) => Path.Combine(ThumbRoot, mediaId.ToString("N") + ".jpg");

    private static string? FindContent(Guid mediaId)
    {
        var dir = Path.Combine(ContentRoot, mediaId.ToString("N"));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir).FirstOrDefault(f => !Path.GetFileName(f).StartsWith('.'));
    }

    private IEnumerable<Entry> Entries()
    {
        var list = new List<Entry>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ContentRoot))
            {
                long size = 0;
                var lastUsed = DateTime.MinValue;
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (Path.GetFileName(file).StartsWith('.')) continue;
                    try
                    {
                        var info = new FileInfo(file);
                        size += info.Length;
                        if (info.LastWriteTimeUtc > lastUsed) lastUsed = info.LastWriteTimeUtc;
                    }
                    catch (IOException)
                    {
                    }
                }
                list.Add(new Entry(dir, true, size, lastUsed));
            }
            foreach (var file in Directory.EnumerateFiles(ThumbRoot))
            {
                if (Path.GetFileName(file).StartsWith('.')) continue;
                try
                {
                    var info = new FileInfo(file);
                    list.Add(new Entry(file, false, info.Length, info.LastWriteTimeUtc));
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not enumerate the media cache");
        }
        return list;
    }

    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception)
        {
            // LRU precision is best effort.
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }

    private void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record Entry(string Path, bool IsDirectory, long Size, DateTime LastUsed);
}
