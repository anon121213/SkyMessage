using System.Globalization;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Rules;

/// <summary>Text, entity and link rules for message content. Pure functions shared by validators and services.</summary>
internal static class MessageContentRules
{
    public const int LanguageMaxLength = 32;

    /// <summary>
    /// True when the text carries nothing visible: null, empty, or only whitespace, control and format
    /// characters (zero-width spaces etc.).
    /// </summary>
    public static bool IsBlank(string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) continue;
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// PostgreSQL text cannot hold NUL, and unpaired UTF-16 surrogates cannot be encoded as UTF-8:
    /// such strings are rejected instead of failing (or being silently altered) in the database.
    /// </summary>
    public static bool HasForbiddenChars(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\0') return true;
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(ch))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Absolute http/https/mailto/tel URL of at most <see cref="Limits.LinkUrlMaxLength"/> chars, no whitespace or credentials.</summary>
    public static bool IsAllowedLinkUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > Limits.LinkUrlMaxLength) return false;
        foreach (var ch in url)
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) return false;
        if (HasForbiddenChars(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!url.StartsWith(uri.Scheme + ":", StringComparison.OrdinalIgnoreCase)) return false;

        return uri.Scheme switch
        {
            // "http://google.com@evil.example" style links hide the real host from the reader.
            "http" or "https" => !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo),
            "mailto" or "tel" => url.Length > uri.Scheme.Length + 1,
            _ => false,
        };
    }

    public static bool IsValidLanguage(string language)
    {
        if (language.Length is 0 or > LanguageMaxLength) return false;
        foreach (var ch in language)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '+' or '#' or '.' or '-') continue;
            return false;
        }
        return true;
    }

    /// <summary>Problems with the text itself (length, forbidden characters). Empty when valid.</summary>
    public static IEnumerable<string> TextErrors(string? text)
    {
        if (text is null) yield break;
        if (text.Length > Limits.MessageTextMaxLength)
            yield return $"Text must be at most {Limits.MessageTextMaxLength} characters.";
        if (HasForbiddenChars(text))
            yield return "Text contains characters that cannot be stored (NUL or unpaired surrogates).";
    }

    /// <summary>
    /// Problems with formatting entities relative to <paramref name="text"/>: count, bounds (UTF-16 units),
    /// known types, TextLink URLs, mention ids and code languages. Empty when valid.
    /// </summary>
    public static IEnumerable<string> EntityErrors(string? text, IReadOnlyList<MessageEntityDto>? entities)
    {
        if (entities is null || entities.Count == 0) yield break;
        if (entities.Count > Limits.MaxEntitiesPerMessage)
        {
            yield return $"At most {Limits.MaxEntitiesPerMessage} entities are allowed.";
            yield break;
        }

        var textLength = text?.Length ?? 0;
        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (e is null)
            {
                yield return $"Entity {i} is missing.";
                continue;
            }
            if (!Enum.IsDefined(e.Type))
                yield return $"Entity {i} has an unknown type.";
            if (e.Offset < 0 || e.Length <= 0 || (long)e.Offset + e.Length > textLength)
                yield return $"Entity {i} is outside of the text.";
            if (e.Type == MessageEntityType.TextLink && !IsAllowedLinkUrl(e.Url))
                yield return $"Entity {i}: a text link must be an absolute http, https, mailto or tel URL of at most {Limits.LinkUrlMaxLength} characters.";
            if (e.Type == MessageEntityType.Mention && e.UserId == Guid.Empty)
                yield return $"Entity {i}: mention user id must not be empty.";
            if (e.Type is MessageEntityType.Pre or MessageEntityType.Code && e.Language is { } language && !IsValidLanguage(language))
                yield return $"Entity {i}: invalid code language.";
        }
    }

    /// <summary>Stored entities: keeps only the fields that mean something for each type.</summary>
    public static List<MessageEntity> ToEntities(IReadOnlyList<MessageEntityDto>? entities)
    {
        if (entities is null || entities.Count == 0) return [];
        var result = new List<MessageEntity>(entities.Count);
        foreach (var e in entities)
        {
            result.Add(new MessageEntity
            {
                Type = e.Type,
                Offset = e.Offset,
                Length = e.Length,
                Url = e.Type == MessageEntityType.TextLink ? e.Url : null,
                UserId = e.Type == MessageEntityType.Mention ? e.UserId : null,
                Language = e.Type is MessageEntityType.Pre or MessageEntityType.Code && !string.IsNullOrEmpty(e.Language) ? e.Language : null,
            });
        }
        return result;
    }

    public static MessageEntityDto ToDto(MessageEntity e) => new()
    {
        Type = e.Type,
        Offset = e.Offset,
        Length = e.Length,
        Url = e.Url,
        UserId = e.UserId,
        Language = e.Language,
    };

    public static MessageEntity Clone(MessageEntity e) => new()
    {
        Type = e.Type,
        Offset = e.Offset,
        Length = e.Length,
        Url = e.Url,
        UserId = e.UserId,
        Language = e.Language,
    };

    public static bool SameEntities(IReadOnlyList<MessageEntity> a, IReadOnlyList<MessageEntity> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Type != y.Type || x.Offset != y.Offset || x.Length != y.Length || x.Url != y.Url
                || x.UserId != y.UserId || x.Language != y.Language)
                return false;
        }
        return true;
    }

    /// <summary>Cuts to at most <paramref name="maxLength"/> UTF-16 units without splitting a surrogate pair.</summary>
    public static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var cut = maxLength;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut];
    }
}
