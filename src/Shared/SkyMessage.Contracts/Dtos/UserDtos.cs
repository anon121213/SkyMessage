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
    /// <summary>
    /// <see cref="ProfileStyle.Colors"/> key the person picked: tints their profile header and their name in groups.
    /// Null = the default look (also for people who blocked the viewer).
    /// </summary>
    public string? ProfileColor { get; init; }
    /// <summary>Only when the owner's birthday privacy lets this viewer see it (the year only if they gave one).</summary>
    public BirthdayDto? Birthday { get; init; }
}

/// <summary>A birthday: day and month, the year is optional.</summary>
public sealed record BirthdayDto
{
    public int Day { get; init; }
    public int Month { get; init; }
    public int? Year { get; init; }
}

/// <summary>A link on a profile (absolute http/https).</summary>
public sealed record ProfileLinkDto
{
    public string Url { get; init; } = "";
    /// <summary>What the link shows instead of its address (optional).</summary>
    public string? Title { get; init; }
}

/// <summary>The profile header's cover: a gradient (<see cref="ProfileStyle.CoverGradients"/>) or a photo.</summary>
public sealed record ProfileCoverDto
{
    public string? Gradient { get; init; }
    /// <summary>A photo the owner uploaded; shown to viewers who may see the owner's profile photo.</summary>
    public Guid? MediaId { get; init; }
}

/// <summary>A photo in a profile's or a group's avatar history (newest first; the current avatar leads).</summary>
public sealed record AvatarPhotoDto
{
    public MediaDto Media { get; init; } = new();
    /// <summary>When it last became the avatar.</summary>
    public DateTimeOffset SetAt { get; init; }
    public bool IsCurrent { get; init; }
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
    /// <summary>The header's cover (a photo only when the viewer may see the owner's profile photo).</summary>
    public ProfileCoverDto? Cover { get; init; }
    public IReadOnlyList<ProfileLinkDto> Links { get; init; } = [];
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
    /// <summary>Who sees my birthday (and gets reminded of it). Always filled in answers; null in a PUT keeps it as it is.</summary>
    public PrivacyLevel? Birthday { get; init; }
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
    public BirthdayDto? Birthday { get; init; }
    /// <summary>See <see cref="UserSummaryDto.ProfileColor"/>.</summary>
    public string? ProfileColor { get; init; }
    public ProfileCoverDto? Cover { get; init; }
    public IReadOnlyList<ProfileLinkDto> Links { get; init; } = [];
}

/// <summary>PATCH semantics: null fields are left unchanged.</summary>
public sealed record UpdateProfileRequest
{
    public string? DisplayName { get; init; }
    /// <summary>Empty string clears the bio.</summary>
    public string? Bio { get; init; }
    public string? Username { get; init; }
    /// <summary><see cref="ProfileStyle.Colors"/> key; empty string goes back to the default look.</summary>
    public string? ProfileColor { get; init; }
    /// <summary>The whole list (at most <see cref="Limits.ProfileLinksMax"/>); empty clears it.</summary>
    public IReadOnlyList<ProfileLinkDto>? Links { get; init; }
}

/// <summary>PUT api/users/me/birthday.</summary>
public sealed record SetBirthdayRequest
{
    public int Day { get; init; }
    public int Month { get; init; }
    public int? Year { get; init; }
}

/// <summary>PUT api/users/me/cover: exactly one of a gradient key or my own processed photo.</summary>
public sealed record SetCoverRequest
{
    public string? Gradient { get; init; }
    public Guid? MediaId { get; init; }
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
    /// <summary>Added by phone-book sync (and not confirmed or renamed by hand since).</summary>
    public bool IsSynced { get; init; }
}

public sealed record AddContactRequest
{
    public Guid UserId { get; init; }
    public string? CustomName { get; init; }
}

public sealed record AddContactsBulkRequest
{
    public IReadOnlyList<AddContactRequest> Contacts { get; init; } = [];
    /// <summary>Contact sync: new contacts are marked synced (people already in the address book stay as they are).</summary>
    public bool Synced { get; init; }
}

/// <summary>DELETE api/contacts/synced.</summary>
public sealed record RemoveSyncedContactsResultDto
{
    public int Removed { get; init; }
}

public sealed record UpdateContactRequest
{
    /// <summary>Null or empty clears the custom name.</summary>
    public string? CustomName { get; init; }
}
