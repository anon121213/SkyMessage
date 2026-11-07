namespace SkyMessage.Client.UI.Shell;

/// <summary>The cards that share the chat list's notice slot, in the order they take it.</summary>
public enum NoticeKind
{
    /// <summary>UpdateNotice: a new version, its download or a plain warning.</summary>
    Update,
    /// <summary>BirthdayCard: a contact's birthday today.</summary>
    Birthday,
    /// <summary>NotificationPermissionPrompt: the browser's "turn on notifications" card.</summary>
    Notifications,
}

/// <summary>
/// The chat list's one place for app notices (ChatListPanel, cascaded to the cards): the cards render in the order of
/// <see cref="NoticeKind"/> and only the first of them with something to show is on screen — CSS hides any card after
/// it until it goes (update, then birthday, then notifications). Each card tells the slot after every render whether it
/// shows, so a card can stay away instead of starting behind another (<see cref="IsTakenBesides"/>).
/// </summary>
public sealed class NoticeSlot
{
    private readonly bool[] _showing = new bool[Enum.GetValues<NoticeKind>().Length];

    public void Report(NoticeKind kind, bool showing) => _showing[(int)kind] = showing;

    /// <summary>A card other than <paramref name="kind"/> shows (or waits for its turn) now.</summary>
    public bool IsTakenBesides(NoticeKind kind)
    {
        for (var i = 0; i < _showing.Length; i++)
            if (i != (int)kind && _showing[i]) return true;
        return false;
    }
}
