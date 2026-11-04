namespace SkyMessage.Domain.Entities;

/// <summary>One of a user's chat folders (Contracts.ChatFolderDto explains the rules; Contracts.ChatFolderRules evaluates them).</summary>
public sealed class ChatFolder
{
    /// <summary>Chosen by the app that creates the folder (a repeated create is the same folder).</summary>
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    /// <summary>Place among the user's folders: lower comes first (gaps are fine).</summary>
    public int Position { get; set; }

    public bool IncludeContacts { get; set; }
    public bool IncludeNonContacts { get; set; }
    public bool IncludeGroups { get; set; }
    public bool IncludeChannels { get; set; }
    public bool ExcludeMuted { get; set; }
    public bool ExcludeRead { get; set; }
    public bool ExcludeArchived { get; set; }

    /// <summary>Native uuid[] columns read with the row: a folder's chat lists are small and always used whole.</summary>
    public List<Guid> IncludedChatIds { get; set; } = [];
    public List<Guid> ExcludedChatIds { get; set; } = [];
    /// <summary>The folder's own pinned chats, top first.</summary>
    public List<Guid> PinnedChatIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A user's chat list settings plus the revision of their folder state. The row (created on the first change) is also the
/// lock that puts changes from several devices in order.
/// </summary>
public sealed class ChatListPreferences
{
    public Guid UserId { get; set; }
    public bool ArchiveNewFromStrangers { get; set; }
    public bool KeepArchived { get; set; } = true;
    public bool HideArchive { get; set; }
    /// <summary>Grows with every change of the folders or these settings (Contracts.ChatFoldersDto.Revision).</summary>
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
