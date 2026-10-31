using SkyMessage.Application.Common.Persistence;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Messaging.Rules;

/// <summary>Splits an "around" page between older and newer messages.</summary>
internal static class PageWindow
{
    /// <summary>
    /// <paramref name="beforeAvailable"/>/<paramref name="afterAvailable"/> are how many rows exist on each side
    /// (fetched with limit + 1). Newer rows (including the anchor) get the larger half; a short side gives its
    /// unused share to the other one.
    /// </summary>
    public static (int BeforeTake, int AfterTake) Balance(int beforeAvailable, int afterAvailable, int limit)
    {
        if (limit <= 0) return (0, 0);
        var afterTake = Math.Min(afterAvailable, limit - Math.Min(beforeAvailable, limit / 2));
        var beforeTake = Math.Min(beforeAvailable, limit - afterTake);
        return (beforeTake, afterTake);
    }

    public static int ClampLimit(int? limit, int defaultLimit, int maxLimit) =>
        Math.Clamp(limit ?? defaultLimit, 1, maxLimit);
}

/// <summary>Patterns for PostgreSQL (I)LIKE with backslash as the escape character.</summary>
internal static class LikePattern
{
    public const string EscapeCharacter = "\\";

    public static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    public static string Contains(string value) => "%" + Escape(value) + "%";
}

internal static class SearchQuery
{
    /// <summary>Trimmed query, or null when it is shorter than <see cref="Limits.SearchQueryMinLength"/> or unusable.</summary>
    public static string? Normalize(string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q) || q.Length < Limits.SearchQueryMinLength || q.Length > 256) return null;
        return MessageContentRules.HasForbiddenChars(q) ? null : q;
    }

    /// <summary>Query for matching people: leading '@' removed (username search).</summary>
    public static string? NormalizeForUsers(string? query)
    {
        var q = Normalize(query)?.TrimStart('@').Trim();
        return q is null || q.Length < Limits.SearchQueryMinLength ? null : q;
    }
}

internal static class ReactionRules
{
    /// <summary>
    /// A reaction is a short emoji-like token: 1..<see cref="Limits.ReactionMaxLength"/> UTF-16 units, no
    /// whitespace/control characters, no ASCII letters (plain words), at least one non-ASCII character.
    /// </summary>
    public static bool IsValid(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji) || emoji.Length > Limits.ReactionMaxLength) return false;
        if (MessageContentRules.HasForbiddenChars(emoji)) return false;
        var hasNonAscii = false;
        foreach (var ch in emoji)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) return false;
            if (ch > '\u007f') hasNonAscii = true;
            else if (char.IsAsciiLetter(ch)) return false;
        }
        return hasNonAscii;
    }
}

internal static class MuteRules
{
    /// <summary>"Muted forever" is stored as a far-future instant (not ±infinity, which round-trips poorly).</summary>
    public static readonly DateTimeOffset Forever = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static bool IsValidDuration(int muteForSeconds) => muteForSeconds >= -1;

    /// <summary>0 = unmute (null), -1 = forever, &gt;0 = for that many seconds.</summary>
    public static DateTimeOffset? MutedUntil(DateTimeOffset now, int muteForSeconds) => muteForSeconds switch
    {
        0 => null,
        -1 => Forever,
        > 0 => now.AddSeconds(muteForSeconds),
        _ => throw new ArgumentOutOfRangeException(nameof(muteForSeconds)),
    };

    public static bool IsMuted(DateTimeOffset? mutedUntil, DateTimeOffset now) => mutedUntil is { } until && until > now;
}

/// <summary>A user's order of pinned chats (<c>ChatMember.PinOrder</c>: higher comes first, 0 = not pinned).</summary>
internal static class PinOrderRules
{
    /// <summary>
    /// The top-first order after a reorder request: the requested chats that are still pinned take the places the
    /// requested chats held, in the requested order; pinned chats left out of the request (pinned meanwhile on another
    /// device, or in the other list — main vs archive) keep their places. Unknown and repeated ids are ignored.
    /// </summary>
    public static IReadOnlyList<Guid> Arrange(IReadOnlyList<Guid> currentTopFirst, IReadOnlyList<Guid> requestedTopFirst)
    {
        var pinned = currentTopFirst.ToHashSet();
        var requested = requestedTopFirst.Where(pinned.Contains).Distinct().ToList();
        var moved = requested.ToHashSet();
        var next = new Queue<Guid>(requested);
        return currentTopFirst.Select(id => moved.Contains(id) ? next.Dequeue() : id).ToList();
    }

    /// <summary>PinOrder of the item at <paramref name="index"/> of a top-first list of <paramref name="count"/> (count … 1).</summary>
    public static long OrderAt(int index, int count) => count - index;

    /// <summary>A new pin goes on top.</summary>
    public static long NextPin(long? highestPinned) => Math.Max(highestPinned ?? 0, 0) + 1;
}

/// <summary>Cursor hand-out rules for <c>GET .../messages/changes</c>.</summary>
internal static class ChangesCursorRules
{
    /// <summary>
    /// More pages: continue right after the last returned item. Caught up: the later of the request cursor
    /// and "now" minus <see cref="SyncCursor.Lag"/> (cursors never move backwards; repeats are deduped by clients).
    /// </summary>
    public static SyncCursor Next(SyncCursor requested, DateTimeOffset now, bool hasMore, DateTimeOffset? lastUpdatedAt, Guid? lastId) =>
        hasMore && lastUpdatedAt is { } at && lastId is { } id
            ? new SyncCursor(at, id)
            : SyncCursor.Max(requested, SyncCursor.ForNow(now));

    /// <summary>The same strict "(UpdatedAt, Id) &gt; cursor" order the database applies.</summary>
    public static bool IsAfter(DateTimeOffset updatedAt, Guid id, SyncCursor cursor) =>
        updatedAt > cursor.UpdatedAt || (updatedAt == cursor.UpdatedAt && id.CompareTo(cursor.Id) > 0);
}
