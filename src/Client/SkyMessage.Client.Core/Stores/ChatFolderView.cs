using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// Unread counter of a folder tab: unread messages, like the dock's badge (a chat with only an unread mention counts one);
/// <see cref="Muted"/> when only muted chats have any (shown grey).
/// </summary>
public readonly record struct FolderBadge(int Count, bool Muted);

/// <summary>
/// A folder's chats on this device: <see cref="ChatFolderRules"/> (the server's evaluation, through a
/// <see cref="ChatFolderMatcher"/>) over the chat list, in the folder's own order — its pinned chats first, top first, then
/// by last activity.
/// </summary>
public static class ChatFolderView
{
    /// <param name="chats">The chats to look at: the main list and the archive (a folder may take in archived chats).</param>
    /// <param name="peerIsContact">Private chats: whether the other person is in my contacts now.</param>
    /// <param name="keepChatId">
    /// A chat kept in the folder although it was read since (the one open next to the list, or last opened from it): an
    /// "unread" folder must not pull that chat from under the reader.
    /// </param>
    public static IReadOnlyList<ChatSummaryDto> ChatsOf(ChatFolderDto folder, IEnumerable<ChatSummaryDto> chats, Func<ChatSummaryDto, bool> peerIsContact,
        DateTimeOffset now, Guid? keepChatId = null) =>
        ChatsOf(new ChatFolderMatcher(folder), chats, peerIsContact, now, keepChatId);

    /// <inheritdoc cref="ChatsOf(ChatFolderDto, IEnumerable{ChatSummaryDto}, Func{ChatSummaryDto, bool}, DateTimeOffset, Guid?)"/>
    public static IReadOnlyList<ChatSummaryDto> ChatsOf(ChatFolderMatcher folder, IEnumerable<ChatSummaryDto> chats, Func<ChatSummaryDto, bool> peerIsContact,
        DateTimeOffset now, Guid? keepChatId = null)
    {
        var pins = PinIndex(folder.Folder);
        var shown = new List<ChatSummaryDto>();
        foreach (var chat in chats)
            if (Shows(folder, chat, peerIsContact(chat), now, keepChatId)) shown.Add(chat);
        shown.Sort((a, b) =>
        {
            var pa = pins.TryGetValue(a.Id, out var ia) ? ia : int.MaxValue;
            var pb = pins.TryGetValue(b.Id, out var ib) ? ib : int.MaxValue;
            if (pa != pb) return pa.CompareTo(pb);
            var activity = b.LastActivityAt.CompareTo(a.LastActivityAt);
            return activity != 0 ? activity : b.Id.CompareTo(a.Id);
        });
        return shown;
    }

    /// <summary>How many of <paramref name="chats"/> the folder takes in (the editor's live preview).</summary>
    public static int Count(ChatFolderDto folder, IEnumerable<ChatSummaryDto> chats, Func<ChatSummaryDto, bool> peerIsContact, DateTimeOffset now)
    {
        var matcher = new ChatFolderMatcher(folder);
        var count = 0;
        foreach (var chat in chats)
            if (matcher.Contains(chat, peerIsContact(chat), now)) count++;
        return count;
    }

    public static bool IsPinned(ChatFolderDto folder, Guid chatId) => folder.PinnedChatIds.Contains(chatId);

    /// <summary>Unread messages of these chats; the muted ones count only when no other chat has anything unread.</summary>
    public static FolderBadge BadgeOf(IEnumerable<ChatSummaryDto> chats, DateTimeOffset now)
    {
        var tally = new Tally();
        foreach (var chat in chats) tally.Add(chat, now);
        return tally.Badge;
    }

    /// <summary>
    /// The badge of every folder (in the order given) over <paramref name="chats"/> — the main list and the archive — in
    /// one pass: the contact check runs once per chat, the folder rules through matchers.
    /// </summary>
    public static IReadOnlyList<FolderBadge> BadgesOf(IReadOnlyList<ChatFolderDto> folders, IReadOnlyList<ChatSummaryDto> chats,
        Func<ChatSummaryDto, bool> peerIsContact, DateTimeOffset now)
    {
        if (folders.Count == 0) return [];
        var matchers = folders.Select(f => new ChatFolderMatcher(f)).ToArray();
        var tallies = new Tally[matchers.Length];
        foreach (var chat in chats)
        {
            // Read chats change no counter: skip their rules (most of a long list).
            if (!ChatFolderRules.IsUnread(chat)) continue;
            var contact = peerIsContact(chat);
            for (var i = 0; i < matchers.Length; i++)
                if (matchers[i].Contains(chat, contact, now)) tallies[i].Add(chat, now);
        }
        return tallies.Select(t => t.Badge).ToArray();
    }

    private static bool Shows(ChatFolderMatcher folder, ChatSummaryDto chat, bool peerIsContact, DateTimeOffset now, Guid? keepChatId)
    {
        if (folder.Contains(chat, peerIsContact, now)) return true;
        return chat.Id == keepChatId && folder.Folder.ExcludeRead
               && ChatFolderRules.Contains(folder.Folder with { ExcludeRead = false }, chat, peerIsContact, now);
    }

    private static Dictionary<Guid, int> PinIndex(ChatFolderDto folder)
    {
        var index = new Dictionary<Guid, int>(folder.PinnedChatIds.Count);
        foreach (var id in folder.PinnedChatIds) index.TryAdd(id, index.Count);
        return index;
    }

    private struct Tally
    {
        private int _loud;
        private int _quiet;

        public void Add(ChatSummaryDto chat, DateTimeOffset now)
        {
            if (!ChatFolderRules.IsUnread(chat)) return;
            // An unread mention in a chat whose messages are read still asks for a look.
            var messages = Math.Max(Math.Max(0, chat.UnreadCount), chat.UnreadMentionCount > 0 ? 1 : 0);
            if (ChatFolderRules.IsMuted(chat, now)) _quiet += messages;
            else _loud += messages;
        }

        public readonly FolderBadge Badge => _loud > 0 ? new FolderBadge(_loud, false) : new FolderBadge(_quiet, _quiet > 0);
    }
}

/// <summary>A recommended folder: its icon key and rules (the app names it in the user's language when it is added).</summary>
public sealed record ChatFolderPreset(string Key, string Icon, ChatFolderDto Rules);

/// <summary>Folders offered with one tap: new people (not in my contacts), contacts, groups, channels, unread.</summary>
public static class ChatFolderPresets
{
    public const string New = "new";
    public const string Contacts = "contacts";
    public const string Groups = "groups";
    public const string Channels = "channels";
    public const string Unread = "unread";

    public static IReadOnlyList<ChatFolderPreset> All { get; } =
    [
        new(New, "new", new ChatFolderDto { IncludeNonContacts = true, ExcludeArchived = true }),
        new(Contacts, "contacts", new ChatFolderDto { IncludeContacts = true, ExcludeArchived = true }),
        new(Groups, "users", new ChatFolderDto { IncludeGroups = true, ExcludeArchived = true }),
        new(Channels, "channels", new ChatFolderDto { IncludeChannels = true, ExcludeArchived = true }),
        new(Unread, "unread", new ChatFolderDto
        {
            IncludeContacts = true,
            IncludeNonContacts = true,
            IncludeGroups = true,
            IncludeChannels = true,
            ExcludeRead = true,
            ExcludeArchived = true,
        }),
    ];

    /// <summary>A new folder made from the preset, named <paramref name="name"/>.</summary>
    public static ChatFolderDto Create(ChatFolderPreset preset, string name) =>
        preset.Rules with { Id = Guid.CreateVersion7(), Name = name, Icon = preset.Icon };

    /// <summary>The preset whose rules the folder has (no chats by id): a recommended folder that is already there.</summary>
    public static ChatFolderPreset? Matching(ChatFolderDto folder) =>
        folder.IncludedChatIds.Count > 0 || folder.ExcludedChatIds.Count > 0 ? null : All.FirstOrDefault(p => SameRules(p.Rules, folder));

    private static bool SameRules(ChatFolderDto a, ChatFolderDto b) =>
        a.IncludeContacts == b.IncludeContacts && a.IncludeNonContacts == b.IncludeNonContacts && a.IncludeGroups == b.IncludeGroups
        && a.IncludeChannels == b.IncludeChannels && a.ExcludeMuted == b.ExcludeMuted && a.ExcludeRead == b.ExcludeRead
        && a.ExcludeArchived == b.ExcludeArchived;
}

/// <summary>
/// The changes the folder store shows before the server answers (the server makes the same ones): pure functions over
/// a state, so a pending change can be laid again on top of a newer state that arrives meanwhile.
/// </summary>
public static class ChatFolderEdits
{
    /// <summary>
    /// A folder saved: an existing one gets the new name, icon and rules (its pins stay); a missing one is added last only
    /// when it is being created — an edit of a folder deleted meanwhile does not bring it back (the server refuses it too).
    /// </summary>
    public static ChatFoldersDto Save(ChatFoldersDto state, ChatFolderDto folder, bool create)
    {
        var list = state.Folders.ToList();
        var index = list.FindIndex(f => f.Id == folder.Id);
        if (index >= 0)
        {
            list[index] = folder with { PinnedChatIds = list[index].PinnedChatIds };
        }
        else
        {
            if (!create || list.Count >= Limits.MaxChatFolders) return state;
            list.Add(folder);
        }
        return state with { Folders = list };
    }

    public static ChatFoldersDto Delete(ChatFoldersDto state, Guid folderId) =>
        state with { Folders = state.Folders.Where(f => f.Id != folderId).ToList() };

    public static ChatFoldersDto Reorder(ChatFoldersDto state, IReadOnlyList<Guid> folderIds)
    {
        var byId = state.Folders.ToDictionary(f => f.Id);
        var arranged = PinnedOrderRules.Arrange(state.Folders.Select(f => f.Id).ToList(), folderIds);
        return state with { Folders = arranged.Select(id => byId[id]).ToList() };
    }

    public static ChatFoldersDto SetPinned(ChatFoldersDto state, Guid folderId, Guid chatId, bool pinned) => Change(state, folderId, f =>
    {
        if (f.PinnedChatIds.Contains(chatId) == pinned) return f;
        return f with { PinnedChatIds = pinned ? [chatId, .. f.PinnedChatIds] : f.PinnedChatIds.Where(id => id != chatId).ToList() };
    });

    public static ChatFoldersDto ReorderPinned(ChatFoldersDto state, Guid folderId, IReadOnlyList<Guid> chatIdsTopFirst) =>
        Change(state, folderId, f => f with { PinnedChatIds = PinnedOrderRules.Arrange(f.PinnedChatIds, chatIdsTopFirst) });

    /// <summary>Only the settings the change sets: a pending one never undoes another device's change of another setting.</summary>
    public static ChatFoldersDto UpdateSettings(ChatFoldersDto state, UpdateChatListSettingsRequest change) =>
        state with { Settings = change.ApplyTo(state.Settings) };

    private static ChatFoldersDto Change(ChatFoldersDto state, Guid folderId, Func<ChatFolderDto, ChatFolderDto> change)
    {
        if (state.Folders.All(f => f.Id != folderId)) return state;
        return state with { Folders = state.Folders.Select(f => f.Id == folderId ? change(f) : f).ToList() };
    }
}
