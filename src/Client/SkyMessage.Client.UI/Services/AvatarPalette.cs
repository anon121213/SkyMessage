using System.Globalization;

namespace SkyMessage.Client.UI.Services;

/// <summary>Placeholder avatar colors (stable per id) and initials.</summary>
public static class AvatarPalette
{
    // Twilight-friendly pairs (docs/DESIGN.md): light top-left → deep bottom-right.
    private static readonly (string From, string To)[] Gradients =
    [
        ("#9C8BFF", "#5B3FF0"),
        ("#7CCBFF", "#3A6EF0"),
        ("#5FE3C7", "#12968C"),
        ("#A3E37A", "#1F9E62"),
        ("#FFC77F", "#F0703B"),
        ("#FFA3CD", "#DB4597"),
        ("#FFA493", "#E04850"),
        ("#DDA8FF", "#9446DA"),
    ];

    /// <summary>Index into the palette: identical on every device for the same id.</summary>
    public static int IndexOf(Guid id)
    {
        if (id == Guid.Empty) return 0;
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        var sum = 0;
        foreach (var b in bytes) sum += b;
        return sum % Gradients.Length;
    }

    /// <summary>CSS background for a placeholder avatar.</summary>
    public static string Background(Guid id)
    {
        var (from, to) = Gradients[IndexOf(id)];
        return $"linear-gradient(145deg, {from} 0%, {to} 100%)";
    }

    /// <summary>Solid color of the palette entry (sender names in groups).</summary>
    public static string Color(Guid id) => Gradients[IndexOf(id)].To;

    /// <summary>"Анна Петрова" → "АП", "anna" → "A", "🙂 Bob" → "🙂B". Empty for blank names.</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var words = name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var first = FirstElement(words[0]);
        if (words.Length == 1) return first.ToUpperInvariant();
        var last = FirstElement(words[^1]);
        return (first + last).ToUpperInvariant();
    }

    private static string FirstElement(string word)
    {
        var trimmed = word.TrimStart('@', '(', '"', '«', '\'');
        if (trimmed.Length == 0) trimmed = word;
        var enumerator = StringInfo.GetTextElementEnumerator(trimmed);
        return enumerator.MoveNext() ? (string)enumerator.Current : "";
    }
}
