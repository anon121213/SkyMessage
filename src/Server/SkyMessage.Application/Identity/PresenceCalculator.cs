using SkyMessage.Contracts;

namespace SkyMessage.Application.Identity;

/// <summary>Privacy level evaluation shared by every identity rule. Blocks are checked by callers first.</summary>
public static class PrivacyRules
{
    /// <summary>Visibility levels (photo, last seen, phone, find by phone, calls): Nobody means nobody.</summary>
    public static bool Allows(PrivacyLevel level, bool ownerHasViewerInContacts) => level switch
    {
        PrivacyLevel.Everybody => true,
        PrivacyLevel.Contacts => ownerHasViewerInContacts,
        _ => false,
    };

    /// <summary>Messages and group invites: Nobody behaves like Contacts.</summary>
    public static bool AllowsAtLeastContacts(PrivacyLevel level, bool ownerHasViewerInContacts) =>
        level == PrivacyLevel.Everybody || ownerHasViewerInContacts;
}

/// <summary>Everything needed to decide what a viewer may know about someone's presence.</summary>
public readonly record struct PresenceFacts(
    PrivacyLevel LastSeenLevel,
    bool IsSelf,
    bool IsDeleted,
    bool OwnerBlockedViewer,
    bool OwnerHasViewerInContacts,
    bool IsOnline,
    DateTimeOffset? LastSeenAt);

/// <summary>
/// Per-viewer presence: visible &amp; online → Online; visible &amp; offline → exact last seen; hidden → never
/// online, no timestamp, only an approximation by recency. Blocked viewers and deleted accounts → LongAgo.
/// </summary>
public static class PresenceCalculator
{
    public static readonly TimeSpan RecentlyWindow = TimeSpan.FromDays(3);
    public static readonly TimeSpan WeekWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan MonthWindow = TimeSpan.FromDays(30);

    public static PresenceDto LongAgo { get; } = new() { Approx = LastSeenApprox.LongAgo };

    public static bool IsVisible(in PresenceFacts facts) =>
        facts.IsSelf || (!facts.OwnerBlockedViewer && PrivacyRules.Allows(facts.LastSeenLevel, facts.OwnerHasViewerInContacts));

    public static PresenceDto For(in PresenceFacts facts, DateTimeOffset now)
    {
        if (facts.IsDeleted || (!facts.IsSelf && facts.OwnerBlockedViewer))
            return LongAgo;
        return IsVisible(facts)
            ? Visible(facts.IsOnline, facts.LastSeenAt)
            : Hidden(facts.IsOnline ? now : facts.LastSeenAt, now);
    }

    public static PresenceDto Visible(bool isOnline, DateTimeOffset? lastSeenAt)
    {
        if (isOnline)
            return new PresenceDto { IsOnline = true, Approx = LastSeenApprox.Online };
        return lastSeenAt is { } seen
            ? new PresenceDto { LastSeenAt = seen, Approx = LastSeenApprox.Exact }
            : LongAgo;
    }

    /// <param name="effectiveLastSeen">Now when the owner is online, otherwise their last-seen time.</param>
    public static PresenceDto Hidden(DateTimeOffset? effectiveLastSeen, DateTimeOffset now) =>
        new() { Approx = Approximate(effectiveLastSeen, now) };

    public static LastSeenApprox Approximate(DateTimeOffset? lastSeenAt, DateTimeOffset now)
    {
        if (lastSeenAt is not { } seen) return LastSeenApprox.LongAgo;
        var age = now - seen;
        if (age <= RecentlyWindow) return LastSeenApprox.Recently;
        if (age <= WeekWindow) return LastSeenApprox.WithinWeek;
        if (age <= MonthWindow) return LastSeenApprox.WithinMonth;
        return LastSeenApprox.LongAgo;
    }
}
