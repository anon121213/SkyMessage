using Android.Content;
using Android.Database;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace SkyMessage.App.Droid;

/// <summary>content:// URI helpers (Photo Picker, document picker and MediaStore items).</summary>
internal static class ContentUriHelper
{
    private static ContentResolver Resolver => Android.App.Application.Context.ContentResolver!;

    public static bool IsContentUri(string? uri) => uri is not null && uri.StartsWith("content://", StringComparison.OrdinalIgnoreCase);

    public static Stream OpenRead(string uri) =>
        Resolver.OpenInputStream(AndroidUri.Parse(uri)!) ?? throw new FileNotFoundException("Content is no longer available", uri);

    public static bool CanRead(string uri)
    {
        try
        {
            using var descriptor = Resolver.OpenAssetFileDescriptor(AndroidUri.Parse(uri)!, "r");
            return descriptor is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Takes a persistable read grant when the picker offered one (survives app restarts).</summary>
    public static void TryPersistReadGrant(AndroidUri uri)
    {
        try
        {
            Resolver.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
        }
        catch (Exception)
        {
            // Not every provider offers persistable grants; the URI stays readable while the process lives.
        }
    }

    public static bool HasPersistedReadGrant(string? uri)
    {
        if (!IsContentUri(uri)) return false;
        try
        {
            foreach (var permission in Resolver.PersistedUriPermissions)
                if (permission.IsReadPermission && string.Equals(permission.Uri?.ToString(), uri, StringComparison.Ordinal)) return true;
        }
        catch (Exception)
        {
        }
        return false;
    }

    public static void ReleaseGrant(string? uri)
    {
        if (!IsContentUri(uri)) return;
        try
        {
            Resolver.ReleasePersistableUriPermission(AndroidUri.Parse(uri)!, ActivityFlags.GrantReadUriPermission);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Display name, size (-1 when unknown) and MIME type of a content URI.</summary>
    public static (string Name, long Size, string ContentType) Describe(AndroidUri uri, string fallbackName)
    {
        string? name = null;
        long size = -1;
        try
        {
            using ICursor? cursor = Resolver.Query(uri, [IOpenableColumns.DisplayName, IOpenableColumns.Size], null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                var nameIndex = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                var sizeIndex = cursor.GetColumnIndex(IOpenableColumns.Size);
                if (nameIndex >= 0 && !cursor.IsNull(nameIndex)) name = cursor.GetString(nameIndex);
                if (sizeIndex >= 0 && !cursor.IsNull(sizeIndex)) size = cursor.GetLong(sizeIndex);
            }
        }
        catch (Exception)
        {
            // Some providers reject the projection; fall back to defaults.
        }

        var type = Resolver.GetType(uri);
        if (string.IsNullOrEmpty(type))
            type = name is null ? "application/octet-stream" : SkyMessage.App.Services.FileNames.ContentTypeFor(name);
        if (string.IsNullOrWhiteSpace(name))
            name = fallbackName + SkyMessage.App.Services.FileNames.ExtensionFor(type);
        return (name, size, type);
    }
}
