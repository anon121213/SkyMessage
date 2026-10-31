using System.Text.RegularExpressions;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Messaging.Rules;

/// <summary>Finds plain-text "@username" mentions that the server resolves to chat members.</summary>
internal static partial class MentionParser
{
    public const int MaxUsernames = 100;

    // "@" must start a word (not "mail@user" or "@@user"); the username follows Limits.UsernamePattern
    // and must not continue with another username character.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_@])@([A-Za-z][A-Za-z0-9_]{4,31})(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameMention();

    /// <summary>
    /// Distinct normalized (lower-case) usernames mentioned as "@username", in order of appearance, skipping
    /// code, pre-formatted, URL and e-mail spans.
    /// </summary>
    public static IReadOnlyList<string> ExtractUsernames(string? text, IReadOnlyList<MessageEntityDto>? entities)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('@')) return [];

        var excluded = entities?
            .Where(e => e is not null && e.Type is MessageEntityType.Code or MessageEntityType.Pre or MessageEntityType.Url or MessageEntityType.Email)
            .Select(e => (Start: e.Offset, End: e.Offset + e.Length))
            .ToList();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in UsernameMention().Matches(text))
        {
            var start = match.Index;
            var end = match.Index + match.Length;
            if (excluded is not null && excluded.Any(x => start < x.End && end > x.Start)) continue;

            var username = match.Groups[1].Value.ToLowerInvariant();
            if (seen.Add(username)) result.Add(username);
            if (result.Count >= MaxUsernames) break;
        }
        return result;
    }
}
