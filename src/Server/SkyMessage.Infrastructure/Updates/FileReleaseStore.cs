using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Options;
using SkyMessage.Application.Updates;

namespace SkyMessage.Infrastructure.Updates;

public static class UpdatesInfrastructure
{
    public static IServiceCollection AddUpdatesInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IReleaseStore, FileReleaseStore>();
        return services;
    }
}

/// <summary>
/// Reads the updates directory (<see cref="UpdatesOptions.Path"/>). The manifest is parsed again only when its
/// timestamp or size changes: CI replaces it with an atomic rename after the APK is in place, so a request sees either
/// the old release or the complete new one.
/// </summary>
internal sealed class FileReleaseStore(IOptions<UpdatesOptions> options, ILogger<FileReleaseStore> logger) : IReleaseStore
{
    private readonly object _gate = new();
    private Snapshot? _cached;

    public ReleaseManifest? ReadManifest()
    {
        if (Root() is not { } root || !IsPlainName(options.Value.ManifestFile)) return null;
        var file = new FileInfo(Path.Combine(root, options.Value.ManifestFile));
        if (!file.Exists) return null;

        lock (_gate)
        {
            if (_cached is { } cached && cached.WriteTimeUtc == file.LastWriteTimeUtc && cached.Length == file.Length)
                return cached.Manifest;
        }

        ReleaseManifest? manifest;
        try
        {
            manifest = ReleaseManifestRules.Parse(File.ReadAllText(file.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being replaced right now: the next request reads the new one.
            logger.LogWarning(ex, "The release manifest could not be read");
            return null;
        }
        if (manifest is null) logger.LogWarning("The release manifest {File} is not valid JSON of the expected shape", file.Name);

        lock (_gate) _cached = new Snapshot(file.LastWriteTimeUtc, file.Length, manifest);
        return manifest;
    }

    public long? PackageLength(string fileName)
    {
        if (Root() is not { } root || !ReleaseManifestRules.IsSafeFileName(fileName)) return null;
        var file = new FileInfo(Path.Combine(root, fileName));
        return file.Exists ? file.Length : null;
    }

    private string? Root()
    {
        var path = options.Value.Path;
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    private static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) && !name.StartsWith('.');

    private sealed record Snapshot(DateTime WriteTimeUtc, long Length, ReleaseManifest? Manifest);
}
