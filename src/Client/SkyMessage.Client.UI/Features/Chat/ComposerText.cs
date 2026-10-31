using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Chat;

/// <summary>The "@query" being typed right before the caret (member suggestions).</summary>
public sealed record MentionQuery(int Start, int End, string Query);

/// <summary>
/// Text-model helpers of the composer: keeps entities (mentions, formatting of an edited message) attached to their
/// text while the user edits around them, finds the "@query" at the caret, adds link entities before sending and
/// trims the text without breaking entity offsets. Offsets are UTF-16 units, as in <see cref="MessageEntityDto"/>.
/// </summary>
public static class ComposerText
{
    /// <summary>
    /// Re-anchors entities after an edit from <paramref name="before"/> to <paramref name="after"/>: the changed region
    /// is found from the common prefix and suffix; entities before it stay, entities after it shift, entities touching
    /// it are dropped (their text is no longer what they described).
    /// </summary>
    public static List<MessageEntityDto> Adjust(string before, string after, IReadOnlyList<MessageEntityDto> entities)
    {
        if (entities.Count == 0 || before == after) return entities.ToList();
        var prefix = 0;
        var max = Math.Min(before.Length, after.Length);
        while (prefix < max && before[prefix] == after[prefix]) prefix++;
        var suffix = 0;
        while (suffix < max - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
        var oldEnd = before.Length - suffix;
        var delta = after.Length - before.Length;

        var result = new List<MessageEntityDto>(entities.Count);
        foreach (var entity in entities)
        {
            var start = entity.Offset;
            var end = entity.Offset + entity.Length;
            if (end <= prefix)
            {
                result.Add(entity);
            }
            else if (start >= oldEnd)
            {
                result.Add(entity with { Offset = start + delta });
            }
            else if (prefix == oldEnd && start < prefix && end > prefix && entity.Type is not (MessageEntityType.Mention or MessageEntityType.Url or MessageEntityType.TextLink))
            {
                // Pure insertion inside a formatted run: the run grows with it.
                result.Add(entity with { Length = entity.Length + delta });
            }
        }
        return result.Where(e => e.Length > 0 && e.Offset >= 0 && e.Offset + e.Length <= after.Length).ToList();
    }

    /// <summary>The "@query" right before <paramref name="caret"/> (letters, digits, underscore; up to 32), or null.</summary>
    public static MentionQuery? FindMentionQuery(string text, int caret, IReadOnlyList<MessageEntityDto> entities)
    {
        if (string.IsNullOrEmpty(text)) return null;
        caret = Math.Clamp(caret, 0, text.Length);
        var i = caret - 1;
        var length = 0;
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '_') && length <= Limits.UsernameMaxLength)
        {
            i--;
            length++;
        }
        if (i < 0 || text[i] != '@' || length > Limits.UsernameMaxLength) return null;
        if (i > 0 && !char.IsWhiteSpace(text[i - 1]) && text[i - 1] is not ('(' or '"' or '«' or '\'')) return null;
        // Already part of an inserted mention.
        if (entities.Any(e => e.Type == MessageEntityType.Mention && i >= e.Offset && i < e.Offset + e.Length)) return null;
        // The word continues after the caret: the user is editing inside it, not typing a new mention.
        if (caret < text.Length && (char.IsLetterOrDigit(text[caret]) || text[caret] == '_')) return null;
        return new MentionQuery(i, caret, text[(i + 1)..caret]);
    }

    /// <summary>Adds Url/Email entities for links in stretches without entities (so the "Links" tab finds them).</summary>
    public static List<MessageEntityDto> WithLinkEntities(string text, IReadOnlyList<MessageEntityDto> entities)
    {
        var result = entities.ToList();
        if (string.IsNullOrEmpty(text) || result.Count >= Limits.MaxEntitiesPerMessage) return result;
        var position = 0;
        foreach (var run in TextLinker.Split(text))
        {
            var start = position;
            position += run.Text.Length;
            if (run.Kind is not (TextRunKind.Url or TextRunKind.Email)) continue;
            var end = start + run.Text.Length;
            if (result.Any(e => start < e.Offset + e.Length && end > e.Offset)) continue;
            result.Add(new MessageEntityDto
            {
                Type = run.Kind == TextRunKind.Url ? MessageEntityType.Url : MessageEntityType.Email,
                Offset = start,
                Length = run.Text.Length,
            });
            if (result.Count >= Limits.MaxEntitiesPerMessage) break;
        }
        return result.OrderBy(e => e.Offset).ToList();
    }

    /// <summary>Trims surrounding whitespace and shifts/clips the entities accordingly.</summary>
    public static (string Text, List<MessageEntityDto> Entities) Trim(string text, IReadOnlyList<MessageEntityDto> entities)
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        var end = text.Length;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        var trimmed = text[start..end];
        var list = new List<MessageEntityDto>();
        foreach (var entity in entities)
        {
            var s = Math.Max(entity.Offset, start) - start;
            var e = Math.Min(entity.Offset + entity.Length, end) - start;
            if (e <= s) continue;
            list.Add(entity with { Offset = s, Length = e - s });
        }
        return (trimmed, list);
    }

    /// <summary>Entities the composer can keep editing (everything except auto-detected links, which are rebuilt on send).</summary>
    public static List<MessageEntityDto> EditableEntities(IReadOnlyList<MessageEntityDto>? entities, int textLength) =>
        (entities ?? [])
        .Where(e => e is not null && e.Type is not (MessageEntityType.Url or MessageEntityType.Email))
        .Where(e => e.Length > 0 && e.Offset >= 0 && e.Offset + e.Length <= textLength)
        .ToList();
}
