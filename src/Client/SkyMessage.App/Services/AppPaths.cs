namespace SkyMessage.App.Services;

/// <summary>
/// On-device folders. Android: offline cache and outbox in the app's private files dir (allowBackup=false),
/// media cache and temp files in the cache dir (the OS may trim it). Apple: everything under
/// Library/Caches, which is excluded from iCloud/iTunes backups.
/// </summary>
internal static class AppPaths
{
    private static readonly Lazy<string> DataRoot = new(() =>
    {
#if ANDROID
        return FileSystem.AppDataDirectory;
#else
        return FileSystem.CacheDirectory;
#endif
    });

    public static string CacheStore => Ensure(Path.Combine(DataRoot.Value, "cache-store"));

    public static string Outbox => Ensure(Path.Combine(DataRoot.Value, "outbox"));

    public static string Media => Ensure(Path.Combine(FileSystem.CacheDirectory, "media"));

    /// <summary>
    /// The user's own sounds (CustomSoundStore): app data, never a cache the OS may trim. On Android this is
    /// files/sounds/, the folder the FileProvider of AndroidManifest.xml shares (res/xml/sound_paths.xml).
    /// </summary>
    public static string Sounds => Ensure(Path.Combine(FileSystem.AppDataDirectory, "sounds"));

    public static string Temp => Ensure(Path.Combine(FileSystem.CacheDirectory, "tmp"));

    public static string NewTempFile(string extension) =>
        Path.Combine(Temp, $"{Guid.NewGuid():N}{(extension.StartsWith('.') ? extension : "." + extension)}");

    /// <summary>Temp files older than a day are leftovers of interrupted compressions/exports.</summary>
    public static void CleanupTemp()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var file in Directory.EnumerateFiles(Temp))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
