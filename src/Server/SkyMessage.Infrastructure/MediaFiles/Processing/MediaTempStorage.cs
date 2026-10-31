using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Options;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <summary>
/// Scratch space for processing: <c>Storage:TempPath</c> ?? <c>{temp}/skymessage</c>, one private directory per
/// job (decrypted user media lives there briefly, so it is created owner-only on Unix).
/// </summary>
internal sealed class MediaTempStorage
{
    internal const string JobPrefix = "job-";

    public MediaTempStorage(IOptions<StorageOptions> options)
    {
        var configured = options.Value.TempPath;
        Root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "skymessage")
            : Path.GetFullPath(configured);
    }

    public string Root { get; }

    public MediaJobDirectory CreateJob(Guid mediaId)
    {
        CreatePrivateDirectory(Root);
        var path = Path.Combine(Root, $"{JobPrefix}{mediaId:N}-{Guid.NewGuid():N}");
        CreatePrivateDirectory(path);
        return new MediaJobDirectory(path);
    }

    /// <summary>Removes job directories left behind by a crash; returns how many were deleted.</summary>
    public int DeleteStaleJobs(TimeSpan maxAge, DateTimeOffset now)
    {
        if (!Directory.Exists(Root)) return 0;
        var deleted = 0;
        foreach (var directory in Directory.EnumerateDirectories(Root, JobPrefix + "*"))
        {
            try
            {
                if (now.UtcDateTime - Directory.GetLastWriteTimeUtc(directory) <= maxAge) continue;
                Directory.Delete(directory, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or already gone; the next run retries.
            }
        }
        return deleted;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

internal sealed class MediaJobDirectory(string path) : IDisposable
{
    public string Path { get; } = path;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the hourly cleanup.
        }
    }
}
