namespace SkyMessage.Contracts;

/// <summary>
/// Choices for how a profile looks, shared by the server (validation) and the apps (rendering). Only keys travel: the
/// apps draw them (docs/DESIGN.md), and an app that does not know a key yet shows the default look.
/// </summary>
public static class ProfileStyle
{
    /// <summary>Profile colors: the app's accent pairs. One tints the profile header and the name in groups.</summary>
    public static IReadOnlyList<string> Colors { get; } = ["violet", "blue", "teal", "green", "orange", "pink", "red", "graphite"];

    /// <summary>Gradient covers for the profile header: pieces of sky.</summary>
    public static IReadOnlyList<string> CoverGradients { get; } = ["dawn", "day", "sunset", "dusk", "night", "mint"];

    public static bool IsColor(string? key) => key is not null && Colors.Contains(key);

    public static bool IsCoverGradient(string? key) => key is not null && CoverGradients.Contains(key);

    /// <summary>
    /// A profile link as stored: trimmed, "https://" added when the scheme is missing ("site.ru/me"), absolute http or
    /// https with a host, at most <see cref="Limits.ProfileLinkUrlMaxLength"/> characters. Null when it is not one.
    /// </summary>
    public static string? NormalizeLinkUrl(string? url)
    {
        var value = url?.Trim();
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace)) return null;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (value.Length > Limits.ProfileLinkUrlMaxLength) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        // A bare word ("hello") is not a site: a host needs a dot, unless it is an address of its own.
        if (!uri.Host.Contains('.') && uri.HostNameType == UriHostNameType.Dns) return null;
        return value;
    }

    /// <summary>A link title as stored: trimmed, null when empty, cut at <see cref="Limits.ProfileLinkTitleMaxLength"/>.</summary>
    public static string? NormalizeLinkTitle(string? title)
    {
        var value = title?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= Limits.ProfileLinkTitleMaxLength ? value : value[..Limits.ProfileLinkTitleMaxLength].TrimEnd();
    }
}

/// <summary>
/// Birthday arithmetic shared by the server (validation) and the apps (reminders, the 🎂 mark, ages). A birthday is a
/// day and a month with an optional year; February 29 falls on February 28 in other years.
/// </summary>
public static class Birthdays
{
    /// <summary>A real date (February 29 needs a leap year when the year is given), not before <see cref="Limits.BirthYearMin"/>, not in the future.</summary>
    /// <param name="today">The caller's date; one day of slack covers time zones ahead of it.</param>
    public static bool IsValid(int day, int month, int? year, DateOnly today)
    {
        if (month is < 1 or > 12 || day < 1) return false;
        var lastDay = year is { } y && y is >= 1 and <= 9999
            ? DateTime.DaysInMonth(y, month)
            : month == 2 ? 29 : DateTime.DaysInMonth(2001, month);
        if (day > lastDay) return false;
        if (year is not { } birthYear) return true;
        return birthYear >= Limits.BirthYearMin && birthYear <= today.Year + 1 && new DateOnly(birthYear, month, day) <= today.AddDays(1);
    }

    /// <summary>The birthday's date in <paramref name="year"/>.</summary>
    public static DateOnly InYear(int day, int month, int year) =>
        month == 2 && day == 29 && !DateTime.IsLeapYear(year) ? new DateOnly(year, 2, 28) : new DateOnly(year, month, day);

    public static bool IsOn(BirthdayDto birthday, DateOnly date) => InYear(birthday.Day, birthday.Month, date.Year) == date;

    /// <summary>The next birthday on or after <paramref name="from"/>.</summary>
    public static DateOnly Next(BirthdayDto birthday, DateOnly from)
    {
        var date = InYear(birthday.Day, birthday.Month, from.Year);
        return date >= from ? date : InYear(birthday.Day, birthday.Month, from.Year + 1);
    }

    /// <summary>Age on <paramref name="date"/>; null without a year.</summary>
    public static int? AgeOn(BirthdayDto birthday, DateOnly date)
    {
        if (birthday.Year is not { } year) return null;
        var age = date.Year - year;
        if (InYear(birthday.Day, birthday.Month, date.Year) > date) age--;
        return Math.Max(0, age);
    }
}
