using System.Globalization;
using System.Text;

namespace SkyMessage.Contracts;

/// <summary>
/// Which chats a folder shows — the one evaluation the server (marking a folder read) and every app (tabs, counters, the
/// editor's preview) run, over the viewer's <see cref="ChatSummaryDto"/>. A chat belongs to a folder when it is not
/// excluded by id, it is included by id or by its type, and none of the folder's exclusions (muted, read, archived)
/// leaves it out: those apply to every chat, the ones included by id too. Over many chats use a
/// <see cref="ChatFolderMatcher"/> (the same rules, the folder's chat lists in hash sets).
/// </summary>
public static class ChatFolderRules
{
    /// <param name="peerIsContact">Private chats: the other person is in my contacts (apps pass what their contact list says now).</param>
    public static bool Contains(ChatFolderDto folder, ChatSummaryDto chat, bool peerIsContact, DateTimeOffset now) =>
        Evaluate(folder, chat, folder.IncludedChatIds.Contains(chat.Id), folder.ExcludedChatIds.Contains(chat.Id), peerIsContact, now);

    internal static bool Evaluate(ChatFolderDto folder, ChatSummaryDto chat, bool includedById, bool excludedById, bool peerIsContact,
        DateTimeOffset now)
    {
        if (excludedById) return false;
        if (!includedById && !IncludesType(folder, chat.Type, peerIsContact)) return false;
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

    // ---- Names ----

    /// <summary>A folder name as it is stored and shown: line breaks and tabs become spaces, then it is trimmed (null → "").</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        StringBuilder? builder = null;
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch is not ('\r' or '\n' or '\t' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029'))
            {
                builder?.Append(ch);
                continue;
            }
            builder ??= new StringBuilder(name, 0, i, name.Length);
            // "\r\n" is one line break.
            if (ch == '\r' && i + 1 < name.Length && name[i + 1] == '\n') i++;
            builder.Append(' ');
        }
        return (builder?.ToString() ?? name).Trim();
    }

    /// <summary>
    /// A normalized name (<see cref="NormalizeName"/>) can be a folder's: 1 to <see cref="Limits.ChatFolderNameMaxLength"/>
    /// UTF-16 units, no control characters or broken surrogates, and something visible — not only spaces and invisible
    /// format characters such as a zero-width space (the tab would show nothing).
    /// </summary>
    public static bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > Limits.ChatFolderNameMaxLength) return false;
        var visible = false;
        for (var i = 0; i < name.Length; i++)
        {
            if (!TryReadCodePoint(name, ref i, out var cp)) return false;
            var category = CharUnicodeInfo.GetUnicodeCategory(cp);
            if (category == UnicodeCategory.Control) return false;
            if (category is not (UnicodeCategory.Format or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator) && !IsBlank(cp))
                visible = true;
        }
        return visible;
    }

    // ---- Icons ----

    /// <summary>
    /// Validates a folder icon: null/empty (the plain folder icon), the key of an app icon (lowercase latin letters, digits
    /// and dashes, starting with a letter) or one emoji (<see cref="IsOneEmoji"/>), at most
    /// <see cref="Limits.ChatFolderIconMaxLength"/> UTF-16 units.
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
        return IsOneEmoji(icon);
    }

    /// <summary>
    /// One emoji: exactly one user-perceived character (an extended grapheme cluster: a ZWJ family, a flag or a skin tone
    /// count as one) with a non-ASCII code point in it. ASCII appears only as the 0–9, # or * that starts a keycap
    /// (1️⃣ = "1" U+FE0F U+20E3). No spaces, controls, combining marks on their own, or invisible format characters —
    /// bidi controls (U+200E/F, U+202A–U+202E, U+2066–U+2069) and zero-width spaces among them; the zero-width joiner and
    /// the tag characters of subdivision flags are part of emoji and allowed.
    /// </summary>
    public static bool IsOneEmoji(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > Limits.ChatFolderIconMaxLength) return false;
        var nonAscii = false;
        var keycapBase = false;
        var keycap = false;
        var first = true;
        for (var i = 0; i < value.Length; i++)
        {
            if (!TryReadCodePoint(value, ref i, out var cp)) return false;
            var leading = first;
            first = false;
            if (cp <= 0x7F)
            {
                if (!leading || cp is not (>= '0' and <= '9' or '#' or '*')) return false;
                keycapBase = true;
                continue;
            }
            nonAscii = true;
            if (cp == 0x20E3) keycap = true;
            if (IsBlank(cp)) return false;
            switch (CharUnicodeInfo.GetUnicodeCategory(cp))
            {
                case UnicodeCategory.Control or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse:
                    return false;
                // The zero-width joiner (U+200D) glues ZWJ sequences; tags (U+E0020–U+E007F) spell subdivision flags.
                case UnicodeCategory.Format when cp is not (0x200D or >= 0xE0020 and <= 0xE007F):
                    return false;
                case UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark when leading:
                    return false;
            }
        }
        if (!nonAscii || keycapBase != keycap) return false;
        return new StringInfo(value).LengthInTextElements == 1;
    }

    /// <summary>Characters that render as nothing although they are not spaces (Braille blank, Hangul fillers).</summary>
    private static bool IsBlank(int cp) => cp is 0x2800 or 0x115F or 0x1160 or 0x3164 or 0xFFA0;

    /// <summary>Reads the code point at <paramref name="i"/> (moving past a surrogate pair); false for a broken surrogate.</summary>
    private static bool TryReadCodePoint(string value, ref int i, out int codePoint)
    {
        var ch = value[i];
        if (char.IsHighSurrogate(ch))
        {
            if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
            {
                codePoint = 0;
                return false;
            }
            codePoint = char.ConvertToUtf32(ch, value[i + 1]);
            i++;
            return true;
        }
        codePoint = ch;
        return !char.IsLowSurrogate(ch);
    }
}

/// <summary>
/// A folder's rules ready to run over many chats (tab counters, a folder's list, marking it read): exactly
/// <see cref="ChatFolderRules.Contains"/>, with the folder's included and excluded chats in hash sets.
/// </summary>
public sealed class ChatFolderMatcher
{
    private readonly HashSet<Guid> _included;
    private readonly HashSet<Guid> _excluded;

    public ChatFolderMatcher(ChatFolderDto folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        Folder = folder;
        _included = [.. folder.IncludedChatIds ?? []];
        _excluded = [.. folder.ExcludedChatIds ?? []];
    }

    public ChatFolderDto Folder { get; }

    /// <param name="peerIsContact">Private chats: the other person is in my contacts.</param>
    public bool Contains(ChatSummaryDto chat, bool peerIsContact, DateTimeOffset now) =>
        ChatFolderRules.Evaluate(Folder, chat, _included.Contains(chat.Id), _excluded.Contains(chat.Id), peerIsContact, now);
}
