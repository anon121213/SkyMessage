namespace SkyMessage.Contracts;

/// <summary>
/// Which chats a folder shows — the one evaluation the server (marking a folder read) and every app (tabs, counters, the
/// editor's preview) run, over the viewer's <see cref="ChatSummaryDto"/>. A chat belongs to a folder when it is not
/// excluded by id, it is included by id or by its type, and none of the folder's exclusions (muted, read, archived)
/// leaves it out: those apply to every chat, the ones included by id too.
/// </summary>
public static class ChatFolderRules
{
    /// <param name="peerIsContact">Private chats: the other person is in my contacts (apps pass what their contact list says now).</param>
    public static bool Contains(ChatFolderDto folder, ChatSummaryDto chat, bool peerIsContact, DateTimeOffset now)
    {
        if (folder.ExcludedChatIds.Contains(chat.Id)) return false;
        if (!folder.IncludedChatIds.Contains(chat.Id) && !IncludesType(folder, chat.Type, peerIsContact)) return false;
        if (folder.ExcludeMuted && IsMuted(chat, now)) return false;
        if (folder.ExcludeRead && !IsUnread(chat)) return false;
        if (folder.ExcludeArchived && chat.IsArchived) return false;
        return true;
    }

    /// <summary>
    /// The folder takes in chats of this type. Saved Messages has no type of its own here: it joins a folder by id only.
    /// Channels will map to <see cref="ChatFolderDto.IncludeChannels"/> once they exist.
    /// </summary>
    public static bool IncludesType(ChatFolderDto folder, ChatType type, bool peerIsContact) => type switch
    {
        ChatType.Private => peerIsContact ? folder.IncludeContacts : folder.IncludeNonContacts,
        ChatType.Group => folder.IncludeGroups,
        _ => false,
    };

    /// <summary>Something to read: unread messages or an unread mention.</summary>
    public static bool IsUnread(ChatSummaryDto chat) => chat.UnreadCount > 0 || chat.UnreadMentionCount > 0;

    public static bool IsMuted(ChatSummaryDto chat, DateTimeOffset now) => chat.MutedUntil is { } until && until > now;

    /// <summary>A folder must take in a type or a chat (one that takes in nothing would always be empty).</summary>
    public static bool TakesInAnything(bool contacts, bool nonContacts, bool groups, bool channels, int includedChats) =>
        contacts || nonContacts || groups || channels || includedChats > 0;

    /// <summary>
    /// Validates a folder icon: null/empty (the plain folder icon), the key of an app icon (lowercase latin letters, digits
    /// and dashes, starting with a letter) or one emoji-like token (no ASCII letters, digits or spaces, at least one
    /// non-ASCII character) of at most <see cref="Limits.ChatFolderIconMaxLength"/> UTF-16 units.
    /// </summary>
    public static bool IsValidIcon(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return true;
        if (icon.Length > Limits.ChatFolderIconMaxLength) return false;
        if (icon[0] is >= 'a' and <= 'z')
        {
            foreach (var ch in icon)
                if (ch is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return false;
            return true;
        }
        var nonAscii = false;
        for (var i = 0; i < icon.Length; i++)
        {
            var ch = icon[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= icon.Length || !char.IsLowSurrogate(icon[i + 1])) return false;
                i++;
                nonAscii = true;
                continue;
            }
            if (char.IsLowSurrogate(ch) || char.IsControl(ch) || char.IsWhiteSpace(ch)) return false;
            if (ch > '\u007f') nonAscii = true;
            else if (char.IsAsciiLetterOrDigit(ch)) return false;
        }
        return nonAscii;
    }
}
