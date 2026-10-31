using System.Globalization;
using System.Text;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Chat;

public enum TextBlockKind
{
    Plain,
    Pre,
    Quote,
}

[Flags]
public enum TextStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strike = 8,
    Code = 16,
    Spoiler = 32,
}

public enum TextLinkKind
{
    None,
    /// <summary>http(s) link; <see cref="TextSpan.Target"/> is the absolute URL.</summary>
    Url,
    /// <summary>mailto: link.</summary>
    Email,
    /// <summary>tel: link.</summary>
    Phone,
    /// <summary>Named link (visible text differs from the target).</summary>
    TextLink,
    /// <summary>Mention of a member by id (<see cref="TextSpan.UserId"/>).</summary>
    Mention,
    /// <summary>"@username" text; <see cref="TextSpan.Target"/> is the username.</summary>
    Username,
}

/// <summary>A run of message text with uniform formatting. Text is rendered as text nodes only.</summary>
public sealed record TextSpan(string Text, TextStyle Style, TextLinkKind Link, string? Target, Guid? UserId, int SpoilerGroup);

/// <summary>A paragraph-level piece of a message: plain flow, a preformatted block or a quote.</summary>
public sealed record TextBlock(TextBlockKind Kind, IReadOnlyList<TextSpan> Spans, string? Language);

/// <summary>
/// Turns untrusted message text + entities into blocks of styled spans (never markup). Entities are clamped to the
/// text and never split surrogate pairs; overlapping blocks are dropped; link targets are re-validated (http, https,
/// mailto, tel only); plain stretches without link entities get safe auto-links (URLs, e-mail, @usernames).
/// </summary>
public static class MessageTextLayout
{
    private const int MaxEntities = 200;

    private sealed record Range(MessageEntityDto Entity, int Start, int End, int Index);

    public static IReadOnlyList<TextBlock> Build(string? text, IReadOnlyList<MessageEntityDto>? entities)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var ranges = Normalize(text, entities);

        // Block entities (pre/quote) partition the text; overlapping ones are ignored.
        var blocks = new List<(TextBlockKind Kind, int Start, int End, string? Language)>();
        var blockRanges = ranges
            .Where(r => r.Entity.Type is MessageEntityType.Pre or MessageEntityType.Quote)
            .OrderBy(r => r.Start)
            .ThenByDescending(r => r.End - r.Start);
        var cursor = 0;
        foreach (var range in blockRanges)
        {
            if (range.Start < cursor) continue;
            if (range.Start > cursor) blocks.Add((TextBlockKind.Plain, cursor, range.Start, null));
            blocks.Add((range.Entity.Type == MessageEntityType.Pre ? TextBlockKind.Pre : TextBlockKind.Quote, range.Start, range.End, range.Entity.Language));
            cursor = range.End;
        }
        if (cursor < text.Length) blocks.Add((TextBlockKind.Plain, cursor, text.Length, null));

        var inline = ranges.Where(r => r.Entity.Type is not (MessageEntityType.Pre or MessageEntityType.Quote)).ToList();
        var result = new List<TextBlock>(blocks.Count);
        foreach (var (kind, start, end, language) in blocks)
        {
            List<TextSpan> spans = kind == TextBlockKind.Pre
                ? new List<TextSpan> { new(TrimBlock(text[start..end]), TextStyle.None, TextLinkKind.None, null, null, 0) }
                : BuildSpans(text, start, end, inline);
            if (kind == TextBlockKind.Quote) spans = TrimQuote(spans);
            if (spans.Count > 0) result.Add(new TextBlock(kind, spans, string.IsNullOrWhiteSpace(language) ? null : language));
        }
        return result;
    }

    /// <summary>Plain text of the message with spoilers kept (copying is an explicit user action).</summary>
    public static string PlainText(string? text) => text ?? "";

    private static List<Range> Normalize(string text, IReadOnlyList<MessageEntityDto>? entities)
    {
        var list = new List<Range>();
        if (entities is null) return list;
        var index = 0;
        foreach (var entity in entities.Take(MaxEntities))
        {
            index++;
            if (entity is null || entity.Length <= 0) continue;
            var start = Math.Clamp(entity.Offset, 0, text.Length);
            var end = (int)Math.Clamp((long)entity.Offset + entity.Length, start, text.Length);
            // Never cut a surrogate pair.
            if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start--;
            if (end > 0 && end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1])) end++;
            if (end <= start) continue;
            list.Add(new Range(entity, start, end, index));
        }
        return list;
    }

    private static List<TextSpan> BuildSpans(string text, int start, int end, List<Range> inline)
    {
        var relevant = inline.Where(r => r.Start < end && r.End > start).ToList();
        var points = new SortedSet<int> { start, end };
        foreach (var r in relevant)
        {
            points.Add(Math.Max(start, r.Start));
            points.Add(Math.Min(end, r.End));
        }

        var spans = new List<TextSpan>();
        var ordered = points.ToList();
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var from = ordered[i];
            var to = ordered[i + 1];
            if (to <= from) continue;
            var piece = text[from..to];
            var style = TextStyle.None;
            var link = TextLinkKind.None;
            string? target = null;
            Guid? userId = null;
            var spoiler = 0;
            var linkRank = int.MaxValue;
            foreach (var r in relevant)
            {
                if (r.Start > from || r.End < to) continue;
                switch (r.Entity.Type)
                {
                    case MessageEntityType.Bold: style |= TextStyle.Bold; break;
                    case MessageEntityType.Italic: style |= TextStyle.Italic; break;
                    case MessageEntityType.Underline: style |= TextStyle.Underline; break;
                    case MessageEntityType.Strikethrough: style |= TextStyle.Strike; break;
                    case MessageEntityType.Code: style |= TextStyle.Code; break;
                    case MessageEntityType.Spoiler:
                        style |= TextStyle.Spoiler;
                        spoiler = spoiler == 0 ? r.Index : spoiler;
                        break;
                    default:
                        var candidate = LinkOf(text, r);
                        if (candidate.Kind != TextLinkKind.None && Rank(candidate.Kind) < linkRank)
                        {
                            linkRank = Rank(candidate.Kind);
                            link = candidate.Kind;
                            target = candidate.Target;
                            userId = candidate.UserId;
                        }
                        break;
                }
            }

            if (link == TextLinkKind.None && (style & TextStyle.Code) == 0)
            {
                // Safe auto-links in stretches the sender did not mark up.
                foreach (var run in TextLinker.Split(piece))
                {
                    var kind = run.Kind switch
                    {
                        TextRunKind.Url => TextLinkKind.Url,
                        TextRunKind.Email => TextLinkKind.Email,
                        TextRunKind.Mention => TextLinkKind.Username,
                        _ => TextLinkKind.None,
                    };
                    spans.Add(new TextSpan(run.Text, style, kind, kind == TextLinkKind.None ? null : run.Target, null, spoiler));
                }
                continue;
            }
            spans.Add(new TextSpan(piece, style, link, target, userId, spoiler));
        }
        return Merge(spans);
    }

    private static int Rank(TextLinkKind kind) => kind switch
    {
        TextLinkKind.TextLink => 0,
        TextLinkKind.Mention => 1,
        TextLinkKind.Username => 2,
        TextLinkKind.Url => 3,
        TextLinkKind.Email => 4,
        TextLinkKind.Phone => 5,
        _ => 9,
    };

    private static (TextLinkKind Kind, string? Target, Guid? UserId) LinkOf(string text, Range r)
    {
        var whole = text[r.Start..r.End];
        switch (r.Entity.Type)
        {
            case MessageEntityType.TextLink:
                return LinkService.TryParse(r.Entity.Url, out var named) ? (TextLinkKind.TextLink, named.AbsoluteUri, null) : (TextLinkKind.None, null, null);
            case MessageEntityType.Url:
                var candidate = string.IsNullOrWhiteSpace(r.Entity.Url) ? whole : r.Entity.Url;
                if (LinkService.TryParse(candidate, out var url) && url.Scheme is "http" or "https") return (TextLinkKind.Url, url.AbsoluteUri, null);
                if (!candidate.Contains("://", StringComparison.Ordinal) && LinkService.TryParse("https://" + candidate.Trim(), out var bare))
                    return (TextLinkKind.Url, bare.AbsoluteUri, null);
                return (TextLinkKind.None, null, null);
            case MessageEntityType.Email:
                var mail = whole.Trim();
                return mail.Contains('@') && !mail.Any(char.IsWhiteSpace) ? (TextLinkKind.Email, "mailto:" + mail, null) : (TextLinkKind.None, null, null);
            case MessageEntityType.Phone:
                var digits = new string(whole.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
                return digits.Count(char.IsAsciiDigit) >= 3 ? (TextLinkKind.Phone, "tel:" + digits, null) : (TextLinkKind.None, null, null);
            case MessageEntityType.Mention when r.Entity.UserId is { } id && id != Guid.Empty:
                return (TextLinkKind.Mention, null, id);
            case MessageEntityType.Mention:
                var name = whole.Trim().TrimStart('@');
                return name.Length > 0 ? (TextLinkKind.Username, name, null) : (TextLinkKind.None, null, null);
            default:
                return (TextLinkKind.None, null, null);
        }
    }

    private static List<TextSpan> Merge(List<TextSpan> spans)
    {
        if (spans.Count < 2) return spans;
        var merged = new List<TextSpan>(spans.Count) { spans[0] };
        for (var i = 1; i < spans.Count; i++)
        {
            var last = merged[^1];
            var next = spans[i];
            if (last.Style == next.Style && last.Link == TextLinkKind.None && next.Link == TextLinkKind.None && last.SpoilerGroup == next.SpoilerGroup)
                merged[^1] = last with { Text = last.Text + next.Text };
            else
                merged.Add(next);
        }
        return merged;
    }

    private static string TrimBlock(string value) => value.Trim('\n', '\r');

    /// <summary>A quote block draws its own margins: drop the line breaks around it.</summary>
    private static List<TextSpan> TrimQuote(List<TextSpan> spans)
    {
        if (spans.Count == 0) return spans;
        var list = spans.ToList();
        list[0] = list[0] with { Text = list[0].Text.TrimStart('\n', '\r') };
        list[^1] = list[^1] with { Text = list[^1].Text.TrimEnd('\n', '\r') };
        return list.Where(s => s.Text.Length > 0).ToList();
    }

    /// <summary>
    /// Number of emoji when the text consists of 1–3 emoji only (and whitespace), else 0. Such messages are shown
    /// large and without a bubble.
    /// </summary>
    public static int JumboEmojiCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return 0;
        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (string.IsNullOrWhiteSpace(element)) continue;
            if (!IsEmoji(element)) return 0;
            if (++count > 3) return 0;
        }
        return count;
    }

    private static bool IsEmoji(string element)
    {
        var rune = element.EnumerateRunes().FirstOrDefault();
        var value = rune.Value;
        if (element.Contains('️') || element.Contains('⃣') || element.Contains('‍')) return value > 0x20;
        if (value >= 0x1F000) return true;
        if (value is >= 0x2600 and <= 0x27BF) return true;
        if (value is >= 0x2B00 and <= 0x2BFF) return true;
        if (value is >= 0x2190 and <= 0x21FF) return false;
        return Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol && value > 0x2000;
    }
}
