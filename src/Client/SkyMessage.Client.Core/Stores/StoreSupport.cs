using System.Globalization;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>Client mirrors of server message rules (type inference, reply previews, mentions).</summary>
internal static class MessageRules
{
    /// <summary>Same inference as the server: no media → Text, photos/videos → Photo/Video/Album, notes alone, files/audio → File/Audio.</summary>
    public static MessageType InferType(IReadOnlyList<MediaKind> kinds)
    {
        if (kinds.Count == 0) return MessageType.Text;
        if (kinds.Count == 1 && kinds[0] == MediaKind.Voice) return MessageType.Voice;
        if (kinds.Count == 1 && kinds[0] == MediaKind.VideoNote) return MessageType.VideoNote;
        if (kinds.All(k => k is MediaKind.Photo or MediaKind.Video))
            return kinds.Count > 1 ? MessageType.Album : kinds[0] == MediaKind.Photo ? MessageType.Photo : MessageType.Video;
        return kinds.All(k => k == MediaKind.Audio) ? MessageType.Audio : MessageType.File;
    }

    public static ReplyPreviewDto ReplyPreview(MessageDto message) => new()
    {
        MessageId = message.Id,
        Seq = message.Seq,
        SenderId = message.SenderId,
        Type = message.Type,
        Text = message.Text is { Length: > Limits.ReplyPreviewTextLength } text ? text[..Limits.ReplyPreviewTextLength] : message.Text,
        Media = message.Attachments.Count > 0 ? message.Attachments[0] : null,
        IsDeleted = message.IsDeleted,
    };

    /// <summary>
    /// Mentions me (Mention entity with my id, or "@myusername" in the text) or replies to one of my messages. The
    /// server resolves more cases; this is only used for instant counters until the next refresh.
    /// </summary>
    public static bool MentionsMe(MessageDto message, Guid me, string? myUsername)
    {
        if (message.SenderId == me) return false;
        if (message.ReplyTo is { SenderId: { } replied } && replied == me) return true;
        if (message.Entities is { } entities && entities.Any(e => e.Type == MessageEntityType.Mention && e.UserId == me)) return true;
        if (string.IsNullOrEmpty(myUsername) || string.IsNullOrEmpty(message.Text)) return false;

        var text = message.Text;
        var needle = "@" + myUsername;
        for (var i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var before = i == 0 ? ' ' : text[i - 1];
            var afterIndex = i + needle.Length;
            var after = afterIndex >= text.Length ? ' ' : text[afterIndex];
            if (!IsUsernameChar(before) && !IsUsernameChar(after)) return true;
        }
        return false;
    }

    private static bool IsUsernameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Sent by me, or a service message about something I did (rename, photo, pin, group call): the server counts
    /// both as read for me (it moves my LastReadSeq to them without a MessagesRead event).
    /// </summary>
    public static bool IsMine(MessageDto message, Guid? me) =>
        me is { } id && (message.SenderId == id || (message.Type == MessageType.System && message.SystemEvent?.ActorId == id));

    /// <summary>My reaction derived from the reactor lists; keeps the previous one when the list is capped.</summary>
    public static string? MyReaction(MessageDto message, Guid me, string? previous)
    {
        foreach (var reaction in message.Reactions)
            if (reaction.UserIds.Contains(me)) return reaction.Emoji;
        if (previous is not null && message.Reactions.Any(r => r.Emoji == previous && r.Count > r.UserIds.Count)) return previous;
        return null;
    }

    /// <summary>Reactions with my reaction moved from <paramref name="previous"/> to <paramref name="next"/> (optimistic update).</summary>
    public static IReadOnlyList<ReactionDto> ApplyMyReaction(IReadOnlyList<ReactionDto> reactions, Guid me, string? previous, string? next)
    {
        var list = reactions.ToList();
        if (previous is not null)
        {
            var index = list.FindIndex(r => r.Emoji == previous);
            if (index >= 0)
            {
                var r = list[index];
                var count = Math.Max(0, r.Count - 1);
                if (count == 0) list.RemoveAt(index);
                else list[index] = r with { Count = count, UserIds = r.UserIds.Where(id => id != me).ToList() };
            }
        }
        if (next is not null)
        {
            var index = list.FindIndex(r => r.Emoji == next);
            if (index >= 0)
            {
                var r = list[index];
                list[index] = r with { Count = r.Count + 1, UserIds = r.UserIds.Prepend(me).Distinct().Take(Limits.MaxReactionUserIds).ToList() };
            }
            else
            {
                list.Add(new ReactionDto { Emoji = next, Count = 1, UserIds = [me] });
            }
        }
        return list;
    }
}

internal static class ChatRules
{
    public static bool IsMuted(ChatSummaryDto chat, DateTimeOffset now) => chat.MutedUntil is { } until && until > now;

    /// <summary>Pinned first (newest pin first), then by last activity (newest first).</summary>
    public static IReadOnlyList<ChatSummaryDto> Sort(IEnumerable<ChatSummaryDto> chats) =>
        chats.OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.IsPinned ? c.PinnedAt ?? DateTimeOffset.MinValue : DateTimeOffset.MinValue)
            .ThenByDescending(c => c.LastActivityAt)
            .ThenByDescending(c => c.Id)
            .ToList();

    public static ChatSummaryDto WithSettings(ChatSummaryDto chat, ChatUserSettingsDto settings) => chat with
    {
        IsPinned = settings.IsPinned,
        PinnedAt = settings.PinnedAt,
        IsArchived = settings.IsArchived,
        MutedUntil = settings.MutedUntil,
    };
}

/// <summary>Country calling code → ISO region, for parsing address-book numbers without '+'.</summary>
internal static class PhoneRegions
{
    // Longest prefixes first (+7 7xx is Kazakhstan, the rest of +7 is Russia).
    private static readonly (string Prefix, string Region)[] Prefixes =
    [
        ("+998", "UZ"), ("+996", "KG"), ("+995", "GE"), ("+994", "AZ"), ("+992", "TJ"), ("+993", "TM"),
        ("+380", "UA"), ("+375", "BY"), ("+374", "AM"), ("+373", "MD"), ("+372", "EE"), ("+371", "LV"), ("+370", "LT"),
        ("+971", "AE"), ("+972", "IL"), ("+855", "KH"), ("+856", "LA"), ("+880", "BD"), ("+886", "TW"), ("+852", "HK"),
        ("+420", "CZ"), ("+421", "SK"), ("+358", "FI"), ("+353", "IE"), ("+351", "PT"), ("+359", "BG"),
        ("+77", "KZ"), ("+7", "RU"), ("+66", "TH"), ("+84", "VN"), ("+62", "ID"), ("+60", "MY"), ("+65", "SG"),
        ("+63", "PH"), ("+95", "MM"), ("+86", "CN"), ("+81", "JP"), ("+82", "KR"), ("+91", "IN"), ("+90", "TR"),
        ("+49", "DE"), ("+44", "GB"), ("+33", "FR"), ("+34", "ES"), ("+39", "IT"), ("+31", "NL"), ("+32", "BE"),
        ("+41", "CH"), ("+43", "AT"), ("+45", "DK"), ("+46", "SE"), ("+47", "NO"), ("+48", "PL"), ("+30", "GR"),
        ("+36", "HU"), ("+40", "RO"), ("+61", "AU"), ("+64", "NZ"), ("+55", "BR"), ("+52", "MX"), ("+54", "AR"),
        ("+20", "EG"), ("+1", "US"),
    ];

    public static string? RegionOf(string? e164)
    {
        if (string.IsNullOrWhiteSpace(e164)) return null;
        var digits = new string(e164.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
        if (!digits.StartsWith('+')) return null;
        foreach (var (prefix, region) in Prefixes)
            if (digits.StartsWith(prefix, StringComparison.Ordinal)) return region;
        return null;
    }

    public static string? Normalize(string? region) =>
        region is { Length: 2 } r && r.All(char.IsAsciiLetter) ? r.ToUpper(CultureInfo.InvariantCulture) : null;
}
