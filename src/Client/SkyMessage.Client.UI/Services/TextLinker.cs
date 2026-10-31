using System.Text.RegularExpressions;

namespace SkyMessage.Client.UI.Services;

public enum TextRunKind
{
    Text,
    Url,
    Email,
    Mention,
}

/// <summary>A piece of plain text; links carry their (allow-listed) target.</summary>
public sealed record TextRun(TextRunKind Kind, string Text, string? Target = null);

/// <summary>
/// Finds links in untrusted plain text (bios, descriptions, captions without entities): http(s) and www URLs,
/// e-mail addresses and @usernames. Output is plain runs — render them as text nodes and open links through
/// <see cref="LinkService"/>; never as HTML.
/// </summary>
public static partial class TextLinker
{
    private const int MaxRuns = 400;

    public static IReadOnlyList<TextRun> Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var runs = new List<TextRun>();
        var position = 0;
        foreach (Match match in LinkRegex().Matches(text))
        {
            if (runs.Count >= MaxRuns) break;
            var value = match.Value;
            var kind = match.Groups["url"].Success ? TextRunKind.Url
                : match.Groups["mail"].Success ? TextRunKind.Email
                : TextRunKind.Mention;
            if (kind == TextRunKind.Url) value = TrimTrailing(value);
            if (value.Length == 0) continue;

            string? target = kind switch
            {
                TextRunKind.Url when LinkService.TryParse(value, out var uri) => uri.AbsoluteUri,
                TextRunKind.Email => "mailto:" + value,
                TextRunKind.Mention => value.TrimStart('@'),
                _ => null,
            };
            if (target is null) continue;

            if (match.Index > position) runs.Add(new TextRun(TextRunKind.Text, text[position..match.Index]));
            runs.Add(new TextRun(kind, value, target));
            position = match.Index + value.Length;
        }
        if (position < text.Length) runs.Add(new TextRun(TextRunKind.Text, text[position..]));
        return runs;
    }

    /// <summary>Drops trailing punctuation that is almost never part of a URL ("see https://x.org.").</summary>
    private static string TrimTrailing(string url)
    {
        var end = url.Length;
        while (end > 0)
        {
            var c = url[end - 1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"' or '»' or '…')
            {
                end--;
                continue;
            }
            if (c == ')' && url[..end].Count(ch => ch == '(') < url[..end].Count(ch => ch == ')'))
            {
                end--;
                continue;
            }
            break;
        }
        return url[..end];
    }

    [GeneratedRegex(
        @"(?<url>\b(?:https?://|www\.)[^\s<>""'«»]{2,2048})|(?<mail>\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,24}\b)|(?<mention>(?<![\w@./])@[A-Za-z][A-Za-z0-9_]{4,31}\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();
}
