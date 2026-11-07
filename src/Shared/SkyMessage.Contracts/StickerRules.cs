using System.Text;

namespace SkyMessage.Contracts;

/// <summary>Sticker pack and sticker rules shared by the server (validation) and the apps (editors, suggestions).</summary>
public static class StickerRules
{
    /// <summary>Characters of a pack link's slug (lower-case letters and digits; the server makes 10 of them).</summary>
    private const string SlugAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";
    public const int SlugLength = 10;

    /// <summary>A pack title as stored: line breaks become spaces, trimmed (like folder names).</summary>
    public static string NormalizeTitle(string? title) => ChatFolderRules.NormalizeName(title);

    /// <summary>
    /// 1..<see cref="Limits.StickerPackTitleMaxLength"/> UTF-16 units, something visible (not only spaces or invisible
    /// characters), no control characters or broken surrogates.
    /// </summary>
    public static bool IsValidTitle(string title)
    {
        if (string.IsNullOrEmpty(title) || title.Length > Limits.StickerPackTitleMaxLength) return false;
        var visible = false;
        for (var i = 0; i < title.Length; i++)
        {
            int cp;
            if (char.IsHighSurrogate(title[i]))
            {
                if (i + 1 >= title.Length || !char.IsLowSurrogate(title[i + 1])) return false;
                cp = char.ConvertToUtf32(title[i], title[i + 1]);
                i++;
            }
            else if (char.IsLowSurrogate(title[i]))
            {
                return false;
            }
            else
            {
                cp = title[i];
            }
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(cp);
            if (category == System.Globalization.UnicodeCategory.Control) return false;
            if (category is not (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.SpaceSeparator
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
                && cp is not (0x2800 or 0x115F or 0x1160 or 0x3164 or 0xFFA0))
                visible = true;
        }
        return visible;
    }

    /// <summary>
    /// The emoji a sticker stands for, as stored: trimmed, duplicates (same <see cref="EmojiKey"/>) dropped, in the order
    /// given. Null when the list is empty, too long or holds anything that is not exactly one emoji.
    /// </summary>
    public static List<string>? NormalizeEmoji(IEnumerable<string?>? emoji)
    {
        if (emoji is null) return null;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in emoji)
        {
            var value = raw?.Trim();
            if (!ChatFolderRules.IsOneEmoji(value)) return null;
            if (seen.Add(EmojiKey(value!))) result.Add(value!);
        }
        return result.Count is > 0 and <= Limits.StickerEmojiMax ? result : null;
    }

    /// <summary>
    /// The emoji without variation selectors and skin tones, so "❤️" finds a sticker saved with "❤" and "👍🏽" one with "👍"
    /// (suggestions compare these keys).
    /// </summary>
    public static string EmojiKey(string emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return "";
        var builder = new StringBuilder(emoji.Length);
        for (var i = 0; i < emoji.Length; i++)
        {
            int cp;
            if (char.IsHighSurrogate(emoji[i]) && i + 1 < emoji.Length && char.IsLowSurrogate(emoji[i + 1]))
            {
                cp = char.ConvertToUtf32(emoji[i], emoji[i + 1]);
                i++;
            }
            else
            {
                cp = emoji[i];
            }
            if (cp is 0xFE0E or 0xFE0F or (>= 0x1F3FB and <= 0x1F3FF)) continue;
            builder.Append(char.ConvertFromUtf32(cp));
        }
        return builder.ToString();
    }

    /// <summary>Does this sticker stand for <paramref name="emoji"/>?</summary>
    public static bool Matches(StickerDto sticker, string emoji)
    {
        var key = EmojiKey(emoji.Trim());
        if (key.Length == 0) return false;
        foreach (var own in sticker.Emoji)
            if (EmojiKey(own) == key) return true;
        return false;
    }

    /// <summary>A slug the server could have made (it checks that the pack exists).</summary>
    public static bool IsValidSlug(string? slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length is < 4 or > 64) return false;
        foreach (var ch in slug)
            if (ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')) return false;
        return true;
    }

    /// <summary>A new random slug: <see cref="SlugLength"/> characters without look-alikes (no l, o, 0, 1).</summary>
    public static string NewSlug()
    {
        Span<char> chars = stackalloc char[SlugLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = SlugAlphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(SlugAlphabet.Length)];
        return new string(chars);
    }

    /// <summary>
    /// The order after a reorder request (installed packs, a pack's stickers): the requested ids that exist take the
    /// places the requested ids held, in the requested order; ids left out (added meanwhile on another device) keep
    /// their places. Unknown and repeated ids are ignored. The same arrangement as pinned chats.
    /// </summary>
    public static IReadOnlyList<Guid> Arrange(IReadOnlyList<Guid> current, IReadOnlyList<Guid> requested)
    {
        var present = current.ToHashSet();
        var wanted = requested.Where(present.Contains).Distinct().ToList();
        var moved = wanted.ToHashSet();
        var next = new Queue<Guid>(wanted);
        return current.Select(id => moved.Contains(id) ? next.Dequeue() : id).ToList();
    }

    /// <summary>The main emoji of a sticker media's preview ("Стикер 😀").</summary>
    public static string MainEmoji(IReadOnlyList<string> emoji) => emoji.Count > 0 ? emoji[0] : "";
}
