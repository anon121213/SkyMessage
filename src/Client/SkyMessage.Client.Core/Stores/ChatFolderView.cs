using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>Unread counter of a folder tab: chats with something unread; <see cref="Muted"/> when only muted ones have (shown grey).</summary>
public readonly record struct FolderBadge(int Count, bool Muted);

/// <summary>
/// A folder's chats on this device: <see cref="ChatFolderRules"/> (the server's evaluation) over the chat list, in the
/// folder's own order — its pinned chats first, top first, then by last activity.
/// </summary>
public static class ChatFolderView
{
    /// <param name="chats">The chats to look at: the main list and the archive (a folder may take in archived chats).</param>
    /// <param name="peerIsContact">Private chats: whether the other person is in my contacts now.</param>
    /// <param name="keepChatId">
    /// A chat kept in the folder although it was read since (the one open next to the list): an "unread" folder must not
    /// pull the open chat from under the reader.
    /// </param>
    public static IReadOnlyList<ChatSummaryDto> ChatsOf(ChatFolderDto folder, IEnumerable<ChatSummaryDto> chats, Func<ChatSummaryDto, bool> peerIsContact,
        DateTimeOffset now, Guid? keepChatId = null)
    {
        var pins = PinIndex(folder);
        return chats
            .Where(c => Shows(folder, c, peerIsContact(c), now, keepChatId))
            .OrderBy(c => pins.TryGetValue(c.Id, out var index) ? index : int.MaxValue)
            .ThenByDescending(c => c.LastActivityAt)
            .ThenByDescending(c => c.Id)
            .ToList();
    }

    /// <summary>How many of <paramref name="chats"/> the folder takes in (the editor's live preview).</summary>
    public static int Count(ChatFolderDto folder, IEnumerable<ChatSummaryDto> chats, Func<ChatSummaryDto, bool> peerIsContact, DateTimeOffset now) =>
        chats.Count(c => ChatFolderRules.Contains(folder, c, peerIsContact(c), now));

    public static bool IsPinned(ChatFolderDto folder, Guid chatId) => folder.PinnedChatIds.Contains(chatId);

    /// <summary>Chats with unread messages or mentions; muted chats count only when no other chat has something unread.</summary>
    public static FolderBadge BadgeOf(IEnumerable<ChatSummaryDto> chats, DateTimeOffset now)
    {
        var loud = 0;
        var quiet = 0;
        foreach (var chat in chats)
        {
            if (!ChatFolderRules.IsUnread(chat)) continue;
            if (ChatFolderRules.IsMuted(chat, now)) quiet++;
            else loud++;
        }
        return loud > 0 ? new FolderBadge(loud, false) : new FolderBadge(quiet, quiet > 0);
    }

    private static bool Shows(ChatFolderDto folder, ChatSummaryDto chat, bool peerIsContact, DateTimeOffset now, Guid? keepChatId)
    {
        if (ChatFolderRules.Contains(folder, chat, peerIsContact, now)) return true;
        return chat.Id == keepChatId && folder.ExcludeRead && ChatFolderRules.Contains(folder with { ExcludeRead = false }, chat, peerIsContact, now);
    }

    private static Dictionary<Guid, int> PinIndex(ChatFolderDto folder)
    {
        var index = new Dictionary<Guid, int>(folder.PinnedChatIds.Count);
        foreach (var id in folder.PinnedChatIds) index.TryAdd(id, index.Count);
        return index;
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
    public static ChatFoldersDto Save(ChatFoldersDto state, ChatFolderDto folder)
    {
        var list = state.Folders.ToList();
        var index = list.FindIndex(f => f.Id == folder.Id);
        if (index >= 0)
        {
            list[index] = folder with { PinnedChatIds = list[index].PinnedChatIds };
        }
        else
        {
            if (list.Count >= Limits.MaxChatFolders) return state;
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

    public static ChatFoldersDto UpdateSettings(ChatFoldersDto state, ChatListSettingsDto settings) => state with { Settings = settings };

    private static ChatFoldersDto Change(ChatFoldersDto state, Guid folderId, Func<ChatFolderDto, ChatFolderDto> change)
    {
        if (state.Folders.All(f => f.Id != folderId)) return state;
        return state with { Folders = state.Folders.Select(f => f.Id == folderId ? change(f) : f).ToList() };
    }
}
