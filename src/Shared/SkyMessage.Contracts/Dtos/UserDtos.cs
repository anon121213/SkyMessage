namespace SkyMessage.Contracts;

/// <summary>Presence as visible to a particular viewer (privacy already applied).</summary>
public sealed record PresenceDto
{
    public bool IsOnline { get; init; }
    /// <summary>Null when the owner hides last-seen from this viewer.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }
    public LastSeenApprox Approx { get; init; } = LastSeenApprox.LongAgo;
}

/// <summary>
/// Compact user card. Viewer-dependent: avatar/presence respect privacy settings and
/// <see cref="ContactName"/> is the viewer's own name for this contact.
/// Display name to show = ContactName ?? DisplayName.
/// </summary>
public sealed record UserSummaryDto
{
    public Guid Id { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? ContactName { get; init; }
    public Guid? AvatarMediaId { get; init; }
    public PresenceDto Presence { get; init; } = new();
    public bool IsContact { get; init; }
    public bool IsDeleted { get; init; }
}

public sealed record UserProfileDto
{
    public UserSummaryDto User { get; init; } = new();
    public string? Bio { get; init; }
    /// <summary>Only present when the owner's phone privacy allows this viewer to see it.</summary>
    public string? PhoneNumber { get; init; }
    public bool IsMutualContact { get; init; }
    public bool IsBlockedByMe { get; init; }
    public bool CanMessage { get; init; }
    public bool CanCall { get; init; }
    /// <summary>Existing private chat between the viewer and this user, if any.</summary>
    public Guid? PrivateChatId { get; init; }
    public int CommonGroupsCount { get; init; }
}

public sealed record PrivacySettingsDto
{
    public PrivacyLevel LastSeen { get; init; } = PrivacyLevel.Everybody;
    public PrivacyLevel ProfilePhoto { get; init; } = PrivacyLevel.Everybody;
    public PrivacyLevel PhoneNumber { get; init; } = PrivacyLevel.Contacts;
    /// <summary>Who can find me by my phone number. Off (Nobody) until the user enables it.</summary>
    public PrivacyLevel FindByPhone { get; init; } = PrivacyLevel.Nobody;
    /// <summary>Who can start a private chat with me (Nobody is treated as Contacts).</summary>
    public PrivacyLevel Messages { get; init; } = PrivacyLevel.Everybody;
    public PrivacyLevel Calls { get; init; } = PrivacyLevel.Everybody;
    /// <summary>Who can add me to groups (Nobody is treated as Contacts).</summary>
    public PrivacyLevel GroupInvites { get; init; } = PrivacyLevel.Everybody;
}

/// <summary>The signed-in user's own full profile.</summary>
public sealed record MeDto
{
    public Guid Id { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? Bio { get; init; }
    public Guid? AvatarMediaId { get; init; }
    public string? PhoneNumber { get; init; }
    public PhoneSource PhoneSource { get; init; }
    public PrivacySettingsDto Privacy { get; init; } = new();
    public Guid SavedMessagesChatId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>PATCH semantics: null fields are left unchanged.</summary>
public sealed record UpdateProfileRequest
{
    public string? DisplayName { get; init; }
    /// <summary>Empty string clears the bio.</summary>
    public string? Bio { get; init; }
    public string? Username { get; init; }
}

public sealed record SetAvatarRequest
{
    public Guid MediaId { get; init; }
}

public sealed record SetPhoneRequest
{
    /// <summary>Any common format; the server normalizes to E.164 (default region RU when no '+').</summary>
    public string PhoneNumber { get; init; } = "";
    public PhoneSource Source { get; init; } = PhoneSource.Manual;
    /// <summary>When set, also updates <see cref="PrivacySettingsDto.FindByPhone"/>.</summary>
    public PrivacyLevel? FindByPhone { get; init; }
    /// <summary>ISO 3166 alpha-2 region for numbers without '+' (SIM/network country or device locale).</summary>
    public string? DefaultRegion { get; init; }
}

public sealed record FindByPhonesRequest
{
    public IReadOnlyList<string> PhoneNumbers { get; init; } = [];
    /// <summary>ISO 3166 alpha-2 region for address-book numbers without '+' (e.g. "TH" for "081 234 5678").</summary>
    public string? DefaultRegion { get; init; }
    /// <summary>
    /// Regions tried in order for numbers without '+' (e.g. both SIM countries of a dual-SIM phone, then
    /// my own phone's region). Takes precedence over <see cref="DefaultRegion"/> when non-empty.
    /// </summary>
    public IReadOnlyList<string> DefaultRegions { get; init; } = [];
}

/// <summary>POST api/users/batch — resolve many ids to viewer-dependent summaries in one call.</summary>
public sealed record GetUsersRequest
{
    /// <summary>At most <see cref="Limits.MaxBatchUsers"/>.</summary>
    public IReadOnlyList<Guid> UserIds { get; init; } = [];
}

public sealed record PhoneMatchDto
{
    /// <summary>The number exactly as it was submitted, so the client can map it back to its address book.</summary>
    public string PhoneNumber { get; init; } = "";
    public UserSummaryDto User { get; init; } = new();
}

public sealed record ContactDto
{
    public UserSummaryDto User { get; init; } = new();
    public string? CustomName { get; init; }
    public bool IsMutual { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}

public sealed record AddContactRequest
{
    public Guid UserId { get; init; }
    public string? CustomName { get; init; }
}

public sealed record AddContactsBulkRequest
{
    public IReadOnlyList<AddContactRequest> Contacts { get; init; } = [];
}

public sealed record UpdateContactRequest
{
    /// <summary>Null or empty clears the custom name.</summary>
    public string? CustomName { get; init; }
}
