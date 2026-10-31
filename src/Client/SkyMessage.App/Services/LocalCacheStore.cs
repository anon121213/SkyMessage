using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Services;

/// <summary>
/// Offline key/value cache as one file per key (lowercase hex of the key as the file name, safe on
/// case-insensitive file systems; very long keys are hashed with the key kept in a ".key" companion). Writes go to a temp file and are moved into place, so
/// a crash never leaves a half-written entry. All I/O runs off the UI thread.
/// </summary>
public sealed class LocalCacheStore(ILogger<LocalCacheStore> logger) : ILocalCacheStore
{
    private const string ValueExtension = ".v";
    private const string KeyExtension = ".key";
    private const int MaxEncodedNameLength = 200;

    private readonly string _root = AppPaths.CacheStore;

    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var path = ValuePath(key);
            if (!File.Exists(path)) return null;
            try
            {
                return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }, cancellationToken);

    public Task WriteAsync(string key, string content, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var path = ValuePath(key);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
                if (IsHashed(key))
                    await File.WriteAllTextAsync(Path.ChangeExtension(path, KeyExtension), key, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(temp);
            }
        }, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var path = ValuePath(key);
            TryDelete(path);
            if (IsHashed(key)) TryDelete(Path.ChangeExtension(path, KeyExtension));
        }, cancellationToken);

    public Task ClearAsync(string prefix = "", CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(_root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    if (prefix.Length == 0) TryDelete(file);
                    continue;
                }
                if (!file.EndsWith(ValueExtension, StringComparison.Ordinal)) continue;
                var key = KeyOf(file);
                if (prefix.Length > 0 && (key is null || !key.StartsWith(prefix, StringComparison.Ordinal))) continue;
                TryDelete(file);
                TryDelete(Path.ChangeExtension(file, KeyExtension));
            }
        }, cancellationToken);

    public Task<long> GetSizeBytesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(_root))
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
        }, cancellationToken);

    private string ValuePath(string key) => Path.Combine(_root, NameOf(key) + ValueExtension);

    private static bool IsHashed(string key) => Encoding.UTF8.GetByteCount(key) * 2 > MaxEncodedNameLength;

    private static string NameOf(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        if (bytes.Length * 2 <= MaxEncodedNameLength) return "k" + Convert.ToHexStringLower(bytes);
        return "h" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private string? KeyOf(string valuePath)
    {
        var name = Path.GetFileNameWithoutExtension(valuePath);
        try
        {
            if (name.StartsWith('k')) return Encoding.UTF8.GetString(Convert.FromHexString(name.AsSpan(1)));
            if (name.StartsWith('h'))
            {
                var keyFile = Path.ChangeExtension(valuePath, KeyExtension);
                return File.Exists(keyFile) ? File.ReadAllText(keyFile, Encoding.UTF8) : null;
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            logger.LogDebug(ex, "Unreadable cache file name {Name}", name);
        }
        return null;
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
}
