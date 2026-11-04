using SkyMessage.Client.Core.Services;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI.Features.Profile;

/// <summary>Localized reminder texts for Core (birthday reminders, friends who joined). Singleton without JS.</summary>
public sealed class ReminderTexts(ILocalizer l) : IReminderTexts
{
    public string BirthdayTitle(string name) => BirthdayTitle(l, [name]);

    /// <summary>
    /// "Сегодня день рождения у Анны и Олега": in Russian the names take the genitive (<see cref="RussianNames"/>);
    /// when one of them cannot be declined for sure, the names follow a colon instead and need no ending
    /// ("Сегодня день рождения: Саша работа"). Other languages: "Birthday today: Anna and Oleg".
    /// </summary>
    public static string BirthdayTitle(ILocalizer l, IReadOnlyList<string> names)
    {
        if (l.Language == "ru" && names.Count > 0)
        {
            var declined = names.Select(RussianNames.Genitive).ToList();
            if (declined.All(n => n is not null)) return l.Format("birthday.card.titleOf", Join(l, declined!));
        }
        return l.Format("birthday.card.title", Join(l, names));
    }

    /// <summary>"Анна", "Анна и Олег", "Анна, Олег и Мария" (as <see cref="FormatService.JoinNames"/>).</summary>
    private static string Join(ILocalizer l, IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} {l["common.and"]} {names[^1]}",
    };

    public string BirthdayBody(int? age) =>
        age is { } years ? $"{l.Plural("birthday.turns", years)}. {l["birthday.card.wish"]}" : l["birthday.card.wish"];

    public string JoinedTitle(string name) => l.Format("contactSync.joinedTitle", name);

    public string JoinedBody() => l["contactSync.joinedBody"];
}
