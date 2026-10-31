using System.Globalization;

namespace SkyMessage.Application.Common.Persistence;

/// <summary>
/// Opaque keyset cursor for <c>GET .../messages/changes</c>: "{updatedAt UTC ticks}:{id:N}". Items are
/// returned strictly after (UpdatedAt, Id). Cursors handed out for "now" lag by <see cref="Lag"/> so rows
/// stamped before but committed after the read are not skipped (clients dedupe repeats).
/// </summary>
public readonly record struct SyncCursor(DateTimeOffset UpdatedAt, Guid Id)
{
    public static readonly TimeSpan Lag = TimeSpan.FromSeconds(10);

    public static SyncCursor ForNow(DateTimeOffset now) => new(now - Lag, Guid.Empty);

    public string Encode() => $"{UpdatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{Id:N}";

    public static bool TryDecode(string? value, out SyncCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(value)) return false;
        var colon = value.IndexOf(':');
        if (colon <= 0) return false;
        if (!long.TryParse(value.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
        if (!Guid.TryParseExact(value.AsSpan(colon + 1), "N", out var id)) return false;
        cursor = new SyncCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }

    /// <summary>The later of two cursors (cursors never move backwards).</summary>
    public static SyncCursor Max(SyncCursor a, SyncCursor b) =>
        a.UpdatedAt > b.UpdatedAt || (a.UpdatedAt == b.UpdatedAt && a.Id.CompareTo(b.Id) >= 0) ? a : b;
}
