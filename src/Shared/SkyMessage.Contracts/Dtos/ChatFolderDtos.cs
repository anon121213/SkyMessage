namespace SkyMessage.Contracts;

/// <summary>
/// One of my chat folders (synced to my devices). Which chats it shows is <see cref="ChatFolderRules.Contains"/>: chat
/// types and chats taken in by id, minus the chats excluded by id and the exclusions (muted / read / archived). The
/// built-in "All chats" is not a folder: apps show it first, with the global pins (<see cref="ChatSummaryDto.PinOrder"/>).
/// Rules are plain booleans (not a flags enum) so that apps built today read folders of a later server that adds kinds.
/// </summary>
public sealed record ChatFolderDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>One emoji, or the key of one of the apps' folder icons (lowercase latin, e.g. "users"); null = the plain folder icon.</summary>
    public string? Icon { get; init; }

    /// <summary>Private chats with people in my contacts.</summary>
    public bool IncludeContacts { get; init; }
    /// <summary>Private chats with people who are not in my contacts.</summary>
    public bool IncludeNonContacts { get; init; }
    public bool IncludeGroups { get; init; }
    /// <summary>Channels (a later release; no chat is a channel yet).</summary>
    public bool IncludeChannels { get; init; }

    /// <summary>Leave out chats whose notifications are off.</summary>
    public bool ExcludeMuted { get; init; }
    /// <summary>Leave out chats without unread messages or mentions.</summary>
    public bool ExcludeRead { get; init; }
    /// <summary>Leave out archived chats.</summary>
    public bool ExcludeArchived { get; init; }

    /// <summary>Chats in the folder whatever their type (the exclusions still apply), at most <see cref="Limits.MaxChatFolderChats"/>.</summary>
    public IReadOnlyList<Guid> IncludedChatIds { get; init; } = [];
    /// <summary>Chats never in the folder, at most <see cref="Limits.MaxChatFolderChats"/>.</summary>
    public IReadOnlyList<Guid> ExcludedChatIds { get; init; } = [];
    /// <summary>
    /// The folder's own pinned chats, top first. They only order the folder (a pinned chat the rules leave out is not
    /// shown); the global pins belong to "All chats".
    /// </summary>
    public IReadOnlyList<Guid> PinnedChatIds { get; init; } = [];
}

/// <summary>Where chats go in my chat list (synced to my devices with the folders).</summary>
public sealed record ChatListSettingsDto
{
    /// <summary>
    /// New chats from strangers start in the archive, muted: a private chat whose first message (or call) comes from
    /// someone who is not in my contacts, and a group someone who is not in my contacts adds me to. Off: they appear in the
    /// list.
    /// </summary>
    public bool ArchiveNewFromStrangers { get; init; }
    /// <summary>
    /// On (the default): archived chats stay archived when new messages arrive. Off: a new message brings an archived chat
    /// back to the list — except a muted one, and a chat from a stranger while <see cref="ArchiveNewFromStrangers"/> is on.
    /// </summary>
    public bool KeepArchived { get; init; } = true;
    /// <summary>No archive row at the top of the chat list (the archive opens from the list's header instead).</summary>
    public bool HideArchive { get; init; }
}

/// <summary>My folders and chat list settings: the whole state, sent after every change (<see cref="IRealtimeClient.ChatFoldersChanged"/>).</summary>
public sealed record ChatFoldersDto
{
    /// <summary>Grows with every change: a copy with a lower revision is older (apps keep the newest they saw).</summary>
    public long Revision { get; init; }
    /// <summary>In my order (the built-in "All chats" comes before them and is not listed).</summary>
    public IReadOnlyList<ChatFolderDto> Folders { get; init; } = [];
    public ChatListSettingsDto Settings { get; init; } = new();
}

/// <summary>
/// PUT <see cref="ApiRoutes.Folders.ById"/>: creates the folder with that id (<see cref="Create"/>; the app picks a new id,
/// so a repeat is harmless) or changes the fields that are set — null leaves a field as it is, so a change made with an app
/// that does not know a newer field never resets it (every field a later version adds is nullable too). Its pins are kept.
/// </summary>
public sealed record SaveChatFolderRequest
{
    /// <summary>
    /// True: a new folder (it is created when that id does not exist yet). False: an edit — a folder that no longer exists
    /// (deleted on another device) answers 404 <see cref="ErrorCodes.FolderNotFound"/> instead of coming back.
    /// </summary>
    public bool Create { get; init; }
    /// <summary>
    /// 1 to <see cref="Limits.ChatFolderNameMaxLength"/> characters (<see cref="ChatFolderRules.NormalizeName"/>: line
    /// breaks become spaces, trimmed). Required to create.
    /// </summary>
    public string? Name { get; init; }
    /// <summary>See <see cref="ChatFolderDto.Icon"/>; "" = the plain folder icon.</summary>
    public string? Icon { get; init; }
    public bool? IncludeContacts { get; init; }
    public bool? IncludeNonContacts { get; init; }
    public bool? IncludeGroups { get; init; }
    public bool? IncludeChannels { get; init; }
    public bool? ExcludeMuted { get; init; }
    public bool? ExcludeRead { get; init; }
    public bool? ExcludeArchived { get; init; }
    /// <summary>Distinct; a chat cannot be both included and excluded. Ids that are not my chats are dropped.</summary>
    public IReadOnlyList<Guid>? IncludedChatIds { get; init; }
    public IReadOnlyList<Guid>? ExcludedChatIds { get; init; }

    /// <summary>Every field the app knows, from the folder as it is edited.</summary>
    public static SaveChatFolderRequest From(ChatFolderDto folder, bool create) => new()
    {
        Create = create,
        Name = folder.Name,
        Icon = folder.Icon ?? "",
        IncludeContacts = folder.IncludeContacts,
        IncludeNonContacts = folder.IncludeNonContacts,
        IncludeGroups = folder.IncludeGroups,
        IncludeChannels = folder.IncludeChannels,
        ExcludeMuted = folder.ExcludeMuted,
        ExcludeRead = folder.ExcludeRead,
        ExcludeArchived = folder.ExcludeArchived,
        IncludedChatIds = folder.IncludedChatIds,
        ExcludedChatIds = folder.ExcludedChatIds,
    };
}

/// <summary>
/// PUT <see cref="ApiRoutes.Folders.Settings"/>: changes the chat list settings that are set (null = as it is), so two
/// devices switching different settings at the same moment both keep their change.
/// </summary>
public sealed record UpdateChatListSettingsRequest
{
    /// <summary>See <see cref="ChatListSettingsDto.ArchiveNewFromStrangers"/>.</summary>
    public bool? ArchiveNewFromStrangers { get; init; }
    /// <summary>See <see cref="ChatListSettingsDto.KeepArchived"/>.</summary>
    public bool? KeepArchived { get; init; }
    /// <summary>See <see cref="ChatListSettingsDto.HideArchive"/>.</summary>
    public bool? HideArchive { get; init; }

    /// <summary>The settings with the fields of this request applied.</summary>
    public ChatListSettingsDto ApplyTo(ChatListSettingsDto settings) => settings with
    {
        ArchiveNewFromStrangers = ArchiveNewFromStrangers ?? settings.ArchiveNewFromStrangers,
        KeepArchived = KeepArchived ?? settings.KeepArchived,
        HideArchive = HideArchive ?? settings.HideArchive,
    };
}

/// <summary>PUT <see cref="ApiRoutes.Folders.Order"/>: my folders in the order I want them.</summary>
public sealed record ReorderChatFoldersRequest
{
    /// <summary>
    /// Folders in order (at most <see cref="Limits.MaxChatFolders"/>). Unknown ids are ignored; folders left out keep their
    /// places and the listed ones fill the rest in this order (like <see cref="ReorderPinnedChatsRequest"/>).
    /// </summary>
    public IReadOnlyList<Guid> FolderIds { get; init; } = [];
}
