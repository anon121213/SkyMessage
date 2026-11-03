using SkyMessage.Client.Core.Services;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI.Features.Profile;

/// <summary>Localized reminder texts for Core (birthday reminders, friends who joined). Singleton without JS.</summary>
public sealed class ReminderTexts(ILocalizer l) : IReminderTexts
{
    // "Сегодня день рождения: Анна": the name follows a colon, so it never needs a case ending.
    public string BirthdayTitle(string name) => l.Format("birthday.card.title", name);

    public string BirthdayBody(int? age) =>
        age is { } years ? $"{l.Plural("birthday.turns", years)}. {l["birthday.card.wish"]}" : l["birthday.card.wish"];

    public string JoinedTitle(string name) => l.Format("contactSync.joinedTitle", name);

    public string JoinedBody() => l["contactSync.joinedBody"];
}
