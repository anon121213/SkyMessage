using SkyMessage.Contracts;

namespace SkyMessage.Domain.Entities;

public sealed class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Username { get; set; } = "";
    /// <summary>Lower-invariant copy of <see cref="Username"/>; unique.</summary>
    public string UsernameNormalized { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Bio { get; set; }
    /// <summary>E.164 ("+79991234567"); unique when not null. Not SMS-verified.</summary>
    public string? PhoneNumber { get; set; }
    public PhoneSource PhoneSource { get; set; }
    public DateTimeOffset? PhoneLinkedAt { get; set; }
    public string PasswordHash { get; set; } = "";
    public Guid? AvatarMediaId { get; set; }
    public UserPrivacy Privacy { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    /// <summary>Account deleted: profile anonymized, username freed ("deleted_{id:N}"), sessions revoked.</summary>
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Birthday day 1..31 and month 1..12 (both or neither), year optional; visibility per <see cref="UserPrivacy.Birthday"/>.</summary>
    public int? BirthDay { get; set; }
    public int? BirthMonth { get; set; }
    public int? BirthYear { get; set; }
    /// <summary><see cref="ProfileStyle.Colors"/> key: tints the profile header and the name in groups.</summary>
    public string? ProfileColor { get; set; }
    /// <summary>Header cover: a <see cref="ProfileStyle.CoverGradients"/> key, or (taking precedence) the user's own photo.</summary>
    public string? CoverGradient { get; set; }
    public Guid? CoverMediaId { get; set; }
    /// <summary>At most <see cref="Limits.ProfileLinksMax"/> links (jsonb).</summary>
    public List<ProfileLink> Links { get; set; } = [];

    /// <summary>Display name stored for deleted accounts (clients may localize it when <see cref="IsDeleted"/>).</summary>
    public const string DeletedDisplayName = "Deleted Account";

    /// <summary>
    /// Database length of <see cref="Username"/> / <see cref="UsernameNormalized"/>: real usernames are at most
    /// <see cref="Limits.UsernameMaxLength"/> chars, tombstones (<see cref="DeletedUsernameFor"/>) are 40.
    /// </summary>
    public const int StoredUsernameMaxLength = 40;

    public static string Normalize(string username) => username.Trim().TrimStart('@').ToLowerInvariant();

    /// <summary>Username of a deleted account: longer than any valid username, so it never collides with one.</summary>
    public static string DeletedUsernameFor(Guid userId) => $"deleted_{userId:N}";
}

/// <summary>Owned by <see cref="User"/> (jsonb list).</summary>
public sealed class ProfileLink
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
}

/// <summary>Owned by <see cref="User"/> (stored as privacy_* columns).</summary>
public sealed class UserPrivacy
{
    public PrivacyLevel LastSeen { get; set; } = PrivacyLevel.Everybody;
    public PrivacyLevel ProfilePhoto { get; set; } = PrivacyLevel.Everybody;
    public PrivacyLevel PhoneNumber { get; set; } = PrivacyLevel.Contacts;
    public PrivacyLevel FindByPhone { get; set; } = PrivacyLevel.Nobody;
    public PrivacyLevel Messages { get; set; } = PrivacyLevel.Everybody;
    public PrivacyLevel Calls { get; set; } = PrivacyLevel.Everybody;
    public PrivacyLevel GroupInvites { get; set; } = PrivacyLevel.Everybody;
    public PrivacyLevel Birthday { get; set; } = PrivacyLevel.Contacts;
}

/// <summary>A signed-in device. Refresh tokens are stored only as SHA-256 hex hashes.</summary>
public sealed class Session
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public string RefreshTokenHash { get; set; } = "";
    /// <summary>
    /// The token rotated away most recently. Presenting it again is a lost-response retry (the same successor is
    /// handed out) unless <see cref="RefreshTokenConfirmedAt"/> shows the client got that response; then it is only
    /// accepted within the grace window after <see cref="RefreshTokenRotatedAt"/>, and later signals theft.
    /// </summary>
    public string? PreviousRefreshTokenHash { get; set; }
    public DateTimeOffset? RefreshTokenRotatedAt { get; set; }
    /// <summary>First use of an access token issued with the current refresh token (claim <c>rtb</c>).</summary>
    public DateTimeOffset? RefreshTokenConfirmedAt { get; set; }
    public DateTimeOffset RefreshTokenExpiresAt { get; set; }
    /// <summary>32-byte HMAC key for media URL signatures (see Contracts.MediaSignature).</summary>
    public byte[] MediaKey { get; set; } = [];

    public string DeviceName { get; set; } = "";
    public ClientPlatform Platform { get; set; }
    public string AppVersion { get; set; } = "";
    public string? OsVersion { get; set; }
    public string? IpAddress { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActiveAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public PushProvider PushProvider { get; set; }
    public string? PushToken { get; set; }
    public byte[]? PushEncryptionKey { get; set; }

    public bool IsActive => RevokedAt is null;
}

/// <summary>Owner saved <see cref="ContactUserId"/> to their contacts (one-directional, like a phone book).</summary>
public sealed class Contact
{
    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }
    public Guid ContactUserId { get; set; }
    public User? ContactUser { get; set; }
    public string? CustomName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ContactSource Source { get; set; }
}

public sealed class Block
{
    public Guid BlockerId { get; set; }
    public Guid BlockedId { get; set; }
    public User? Blocked { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
