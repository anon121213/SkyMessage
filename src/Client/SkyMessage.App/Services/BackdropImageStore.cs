using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.App.Services;

/// <summary>
/// The user's background pictures as files in the app's private data dir (<see cref="AppPaths.Backdrops"/>;
/// allowBackup=false), served to the page at "/_sm/backdrops/{file}" (MainPage hands app-origin requests here), so
/// boot.js can show the app's picture before Blazor starts.
/// </summary>
public sealed class BackdropImageStore(ILogger<BackdropImageStore> logger) : IBackdropImageStore
{
    public const string PathPrefix = "/_sm/backdrops/";

    public bool IsSupported => true;

    public string PageSource(string fileName) => PathPrefix + fileName;

    public async Task SaveAsync(string fileName, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        var path = PathOf(fileName) ?? throw new ArgumentException("Not a background picture name.", nameof(fileName));
        var partial = path + ".part";
        await File.WriteAllBytesAsync(partial, content, cancellationToken).ConfigureAwait(false);
        File.Move(partial, path, overwrite: true);
    }

    public Task DeleteAsync(string fileName)
    {
        try
        {
            if (PathOf(fileName) is { } path) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete a background picture");
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<string> names = Directory.EnumerateFiles(AppPaths.Backdrops)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => PathOf(name) is not null)
                .ToList();
            return Task.FromResult(names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not list the background pictures");
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    /// <summary>Answers "/_sm/backdrops/{file}" on the app origin. True when the request was one of ours.</summary>
    public bool TryServe(WebViewWebResourceRequestedEventArgs e)
    {
        var uri = e.Uri;
        if (uri is null || !uri.IsAbsoluteUri || !string.Equals(uri.Host, "0.0.0.1", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.AbsolutePath.StartsWith(PathPrefix, StringComparison.Ordinal)) return false;

        e.Handled = true;
        try
        {
            var name = uri.AbsolutePath[PathPrefix.Length..];
            var path = PathOf(name);
            if (path is null || !File.Exists(path))
            {
                e.SetResponse(404, "Not Found", new Dictionary<string, string> { ["Content-Type"] = "text/plain" }, Stream.Null);
                return true;
            }
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = BackdropRules.Extensions[Path.GetExtension(name)],
                // A picture never changes under its name: a new one gets a new name.
                ["Cache-Control"] = "private, max-age=31536000, immutable",
            };
            e.SetResponse(200, "OK", headers, File.OpenRead(path));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A background picture could not be served");
            e.SetResponse(500, "Internal Server Error", new Dictionary<string, string> { ["Content-Type"] = "text/plain" }, Stream.Null);
        }
        return true;
    }

    /// <summary>The file of a well-formed picture name ("0a1b2c3d4e5f.jpg"), else null.</summary>
    private static string? PathOf(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var id = Path.GetFileNameWithoutExtension(fileName);
        var photo = new BackdropPhoto { Id = id, Extension = extension };
        return BackdropRules.IsWellFormed(photo) && photo.FileName == fileName ? Path.Combine(AppPaths.Backdrops, fileName) : null;
    }
}
