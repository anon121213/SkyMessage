using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Localized formatting of times, dates, durations, sizes, presence and typing phrases. Uses our own month and
/// weekday names (common.json), so it does not depend on ICU culture data being present (WebAssembly).
/// Singleton without JS dependencies. All times are shown in the device's local time zone.
/// </summary>
public sealed class FormatService(ILocalizer localizer, IServiceProvider services)
{
    private IUserStore? _users;

    private IUserStore? Users => _users ??= services.GetService<IUserStore>();

    private bool IsRussian => localizer.Language == Localizer.Russian;

    /// <summary>Overridable clock for tests.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    // ---- Times and dates ----

    /// <summary>"14:05" (24 h in Russian; the device's English pattern otherwise).</summary>
    public string Time(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        if (IsRussian) return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var pattern = localizer.Culture.DateTimeFormat.ShortTimePattern;
        try
        {
            return local.ToString(string.IsNullOrEmpty(pattern) ? "h:mm tt" : pattern, localizer.Culture);
        }
        catch (FormatException)
        {
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Chat list: today → time, last 6 days → weekday, this year → "12 мар", else "12.03.2024".</summary>
    public string ChatListTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = Now().ToLocalTime().Date;
        var date = local.Date;
        if (date == today) return Time(value);
        if (date > today.AddDays(-7) && date < today) return localizer[$"date.weekday.short.{(int)date.DayOfWeek}"];
        if (date.Year == today.Year) return DayMonthShort(date);
        return IsRussian
            ? date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>Date separator in a chat: "Сегодня", "Вчера", "12 марта", "12 марта 2024".</summary>
    public string DateChip(DateTimeOffset value)
    {
        var date = value.ToLocalTime().Date;
        var today = Now().ToLocalTime().Date;
        if (date == today) return localizer["date.today"];
        if (date == today.AddDays(-1)) return localizer["date.yesterday"];
        return date.Year == today.Year ? DayMonthLong(date) : DayMonthYear(date);
    }

    /// <summary>"12 марта 2024, 14:05" (or "сегодня в 14:05").</summary>
    public string DateTime(DateTimeOffset value)
    {
        var date = value.ToLocalTime().Date;
        var today = Now().ToLocalTime().Date;
        if (date == today) return localizer.Format("date.todayAt", Time(value));
        if (date == today.AddDays(-1)) return localizer.Format("date.yesterdayAt", Time(value));
        var day = date.Year == today.Year ? DayMonthLong(date) : DayMonthYear(date);
        return localizer.Format("date.dateAt", day, Time(value));
    }

    /// <summary>"12 марта" / "March 12".</summary>
    public string DayMonthLong(DateTime date) =>
        IsRussian
            ? $"{date.Day} {localizer[$"date.month.gen.{date.Month}"]}"
            : $"{localizer[$"date.month.long.{date.Month}"]} {date.Day}";

    /// <summary>"12 мар" / "Mar 12".</summary>
    public string DayMonthShort(DateTime date) =>
        IsRussian
            ? $"{date.Day} {localizer[$"date.month.short.{date.Month}"]}"
            : $"{localizer[$"date.month.short.{date.Month}"]} {date.Day}";

    /// <summary>"12 марта 2024" / "March 12, 2024".</summary>
    public string DayMonthYear(DateTime date) =>
        IsRussian ? $"{DayMonthLong(date)} {date.Year}" : $"{DayMonthLong(date)}, {date.Year}";

    /// <summary>"март 2024" / "March 2024" (month headers in galleries and history).</summary>
    public string MonthYear(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return $"{localizer[$"date.month.long.{local.Month}"]} {local.Year}";
    }

    // ---- Durations ----

    /// <summary>"0:07", "12:34", "1:02:03".</summary>
    public string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var total = (long)Math.Floor(value.TotalSeconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}"
            : $"{minutes}:{seconds:00}";
    }

    public string Duration(int seconds) => Duration(TimeSpan.FromSeconds(seconds));

    public string DurationMs(int? milliseconds) => Duration(TimeSpan.FromMilliseconds(milliseconds ?? 0));

    /// <summary>Spoken style: "45 с", "2 мин", "1 ч 5 мин" (call logs, history).</summary>
    public string DurationWords(int seconds)
    {
        if (seconds < 0) seconds = 0;
        if (seconds < 60) return localizer.Format("duration.seconds", seconds);
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        if (hours == 0) return localizer.Format("duration.minutes", minutes);
        return minutes == 0
            ? localizer.Format("duration.hours", hours)
            : localizer.Format("duration.hoursMinutes", hours, minutes);
    }

    // ---- Sizes and numbers ----

    /// <summary>"512 Б", "1,4 МБ", "2.1 GB".</summary>
    public string FileSize(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = ["size.b", "size.kb", "size.mb", "size.gb", "size.tb"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var text = unit == 0 || value >= 100
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : value.ToString(value >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture);
        if (IsRussian) text = text.Replace('.', ',');
        return $"{text} {localizer[units[unit]]}";
    }

    /// <summary>"1 234" (ru) / "1,234" (en).</summary>
    public string Number(long value)
    {
        var text = value.ToString("#,0", CultureInfo.InvariantCulture);
        return IsRussian ? text.Replace(',', ' ') : text;
    }

    /// <summary>Badge text: "7", "99+".</summary>
    public static string BadgeCount(int count, int max = 99) => count > max ? $"{max}+" : count.ToString(CultureInfo.InvariantCulture);

    // ---- People ----

    /// <summary>"в сети", "был(а) в сети 5 минут назад", "был(а) в сети недавно"...</summary>
    public string Presence(PresenceDto? presence, bool isDeleted = false)
    {
        if (isDeleted) return localizer["presence.deleted"];
        if (presence is null) return localizer["presence.longAgo"];
        if (presence.IsOnline || presence.Approx == LastSeenApprox.Online) return localizer["presence.online"];
        if (presence.LastSeenAt is { } seen)
        {
            var now = Now();
            var ago = now - seen;
            if (ago < TimeSpan.FromMinutes(1)) return localizer["presence.justNow"];
            if (ago < TimeSpan.FromHours(1)) return localizer.Plural("presence.minutesAgo", (long)ago.TotalMinutes);
            var date = seen.ToLocalTime().Date;
            var today = now.ToLocalTime().Date;
            if (date == today) return localizer.Format("presence.todayAt", Time(seen));
            if (date == today.AddDays(-1)) return localizer.Format("presence.yesterdayAt", Time(seen));
            var day = date.Year == today.Year ? DayMonthLong(date) : DayMonthYear(date);
            return localizer.Format("presence.dateAt", day, Time(seen));
        }
        return presence.Approx switch
        {
            LastSeenApprox.Recently => localizer["presence.recently"],
            LastSeenApprox.WithinWeek => localizer["presence.withinWeek"],
            LastSeenApprox.WithinMonth => localizer["presence.withinMonth"],
            _ => localizer["presence.longAgo"],
        };
    }

    public string Presence(UserSummaryDto user) => Presence(user.Presence, user.IsDeleted);

    /// <summary>"5 участников" / "5 members".</summary>
    public string Members(int count) => localizer.Plural("plural.members", count);

    /// <summary>
    /// Typing line for a chat header or the chat list: "печатает…" (private), "Анна печатает…",
    /// "Анна и Олег печатают…", "3 человека печатают…"; recording/uploading actions get their own verbs.
    /// Null when nobody is typing.
    /// </summary>
    public string? Typing(IReadOnlyList<TypingEvent> typing, bool withNames)
    {
        var active = typing.Where(t => t.Action != TypingAction.Cancel).ToList();
        if (active.Count == 0) return null;
        var action = active[0].Action;
        var verbKey = action switch
        {
            TypingAction.RecordingVoice => "typing.voice",
            TypingAction.RecordingVideoNote => "typing.videoNote",
            TypingAction.UploadingPhoto => "typing.photo",
            TypingAction.UploadingVideo => "typing.video",
            TypingAction.UploadingFile => "typing.file",
            _ => "typing.text",
        };
        if (!withNames) return localizer[verbKey];
        var names = active.Select(t => FirstName(Users?.DisplayNameOf(t.UserId) ?? "")).Distinct().ToList();
        return names.Count switch
        {
            1 => localizer.Format($"{verbKey}.one", names[0]),
            2 => localizer.Format($"{verbKey}.two", names[0], names[1]),
            _ => localizer.Plural($"{verbKey}.people", names.Count),
        };
    }

    /// <summary>First word of a name ("Анна Петрова" → "Анна").</summary>
    public static string FirstName(string name)
    {
        var trimmed = name.Trim();
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    /// <summary>Joins names: "Анна", "Анна и Олег", "Анна, Олег и Мария".</summary>
    public string JoinNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return "";
        if (names.Count == 1) return names[0];
        var and = localizer["common.and"];
        return $"{string.Join(", ", names.Take(names.Count - 1))} {and} {names[^1]}";
    }
}
