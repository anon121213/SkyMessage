using System.Text;

namespace SkyMessage.Client.UI.Features.Settings;

/// <summary>A country for manual phone entry: ISO 3166 alpha-2 code (sent as the parsing region) and dialing code.</summary>
public sealed record PhoneRegion(string Code, string DialCode)
{
    /// <summary>Localization key of the country name.</summary>
    public string NameKey => $"settings.country.{Code}";
}

/// <summary>
/// Countries offered for manual phone entry: the family's own (Russia, Thailand), neighbours and common travel and
/// work destinations. The server parses and validates numbers (libphonenumber); the region only matters for numbers
/// typed without "+".
/// </summary>
public static class PhoneRegions
{
    public static IReadOnlyList<PhoneRegion> All { get; } =
    [
        new("RU", "+7"), new("TH", "+66"), new("KZ", "+7"), new("BY", "+375"), new("UA", "+380"), new("UZ", "+998"),
        new("KG", "+996"), new("TJ", "+992"), new("TM", "+993"), new("AM", "+374"), new("GE", "+995"), new("AZ", "+994"),
        new("MD", "+373"), new("MN", "+976"), new("TR", "+90"), new("AE", "+971"), new("IL", "+972"), new("CY", "+357"),
        new("RS", "+381"), new("ME", "+382"), new("DE", "+49"), new("FR", "+33"), new("IT", "+39"), new("ES", "+34"),
        new("PT", "+351"), new("NL", "+31"), new("AT", "+43"), new("CH", "+41"), new("PL", "+48"), new("CZ", "+420"),
        new("FI", "+358"), new("EE", "+372"), new("LV", "+371"), new("LT", "+370"), new("GB", "+44"), new("US", "+1"),
        new("CA", "+1"), new("CN", "+86"), new("HK", "+852"), new("JP", "+81"), new("KR", "+82"), new("VN", "+84"),
        new("LA", "+856"), new("KH", "+855"), new("MM", "+95"), new("MY", "+60"), new("SG", "+65"), new("ID", "+62"),
        new("PH", "+63"), new("IN", "+91"), new("LK", "+94"), new("AU", "+61"),
    ];

    public static PhoneRegion? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : All.FirstOrDefault(r => string.Equals(r.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The region of an E.164 number by its dialing code (the longest match; "+7" → Russia).</summary>
    public static PhoneRegion? OfNumber(string? e164)
    {
        if (string.IsNullOrEmpty(e164) || !e164.StartsWith('+')) return null;
        return All.Where(r => e164.StartsWith(r.DialCode, StringComparison.Ordinal))
            .OrderByDescending(r => r.DialCode.Length)
            .FirstOrDefault();
    }
}

/// <summary>Readable phone numbers ("+7 916 123-45-67", "+66 81 234 5678"). Display only; the server stores E.164.</summary>
public static class PhoneFormat
{
    public static string Display(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return "";
        var text = number.Trim();
        if (!text.StartsWith('+') || text.Length < 8 || !text.Skip(1).All(char.IsAsciiDigit)) return text;
        var region = PhoneRegions.OfNumber(text);
        var code = region?.DialCode ?? text[..2];
        var rest = text[code.Length..];
        if (code == "+7" && rest.Length == 10)
            return $"{code} {rest[..3]} {rest[3..6]}-{rest[6..8]}-{rest[8..]}";
        if (code == "+66" && rest.Length == 9)
            return $"{code} {rest[..2]} {rest[2..5]} {rest[5..]}";
        var builder = new StringBuilder(code);
        for (var i = 0; i < rest.Length; i += 3)
        {
            var take = rest.Length - i == 4 ? 4 : Math.Min(3, rest.Length - i);
            builder.Append(' ').Append(rest, i, take);
            if (take == 4) break;
        }
        return builder.ToString();
    }
}
