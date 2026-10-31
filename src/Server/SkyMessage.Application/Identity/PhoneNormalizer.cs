using PhoneNumbers;

namespace SkyMessage.Application.Identity;

/// <summary>
/// Phone numbers in, E.164 out (libphonenumber). Numbers without '+' are read in a region: the one the
/// client reports (SIM/network/locale), else the caller's own number's region, else <see cref="FallbackRegion"/>.
/// </summary>
public static class PhoneNormalizer
{
    public const string FallbackRegion = "RU";
    public const int MaxInputLength = 64;
    private const int MinPhoneDigits = 5;

    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    /// <summary>Upper-case ISO 3166 alpha-2 code known to libphonenumber, or null.</summary>
    public static string? NormalizeRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region)) return null;
        var code = region.Trim().ToUpperInvariant();
        return code.Length == 2 && Util.GetSupportedRegions().Contains(code) ? code : null;
    }

    /// <summary>The first usable region among <paramref name="candidates"/>, else <see cref="FallbackRegion"/>.</summary>
    public static string ResolveRegion(params ReadOnlySpan<string?> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (NormalizeRegion(candidate) is { } region)
                return region;
        }
        return FallbackRegion;
    }

    /// <summary>E.164 ("+79991234567") of a valid number, or null.</summary>
    public static string? Normalize(string? input, string? region)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();
        if (text.Length > MaxInputLength) return null;
        try
        {
            var number = Util.Parse(text, NormalizeRegion(region) ?? FallbackRegion);
            return Util.IsValidNumber(number) ? Util.Format(number, PhoneNumberFormat.E164) : null;
        }
        catch (NumberParseException)
        {
            return null;
        }
    }

    /// <summary>Region of an E.164 number ("RU", "TH"), or null.</summary>
    public static string? RegionOf(string? e164)
    {
        if (string.IsNullOrEmpty(e164)) return null;
        try
        {
            var region = Util.GetRegionCodeForNumber(Util.Parse(e164, FallbackRegion));
            return NormalizeRegion(region);
        }
        catch (NumberParseException)
        {
            return null;
        }
    }

    /// <summary>
    /// A login is a phone number when it starts with '+' or has no letters and at least a few digits
    /// (usernames always start with a Latin letter).
    /// </summary>
    public static bool LooksLikePhone(string login)
    {
        var text = login.Trim();
        if (text.Length == 0) return false;
        if (text[0] == '+') return true;
        var digits = 0;
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c)) digits++;
            else if (char.IsLetter(c)) return false;
        }
        return digits >= MinPhoneDigits;
    }

    /// <summary>E.164 candidates for a phone login: parsed in <paramref name="defaultRegion"/> first, then in RU.</summary>
    public static IReadOnlyList<string> LoginCandidates(string login, string? defaultRegion)
    {
        var result = new List<string>(2);
        if (NormalizeRegion(defaultRegion) is { } region && Normalize(login, region) is { } preferred)
            result.Add(preferred);
        if (Normalize(login, FallbackRegion) is { } fallback && !result.Contains(fallback))
            result.Add(fallback);
        return result;
    }
}
