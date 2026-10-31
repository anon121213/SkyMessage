namespace SkyMessage.Contracts;

public enum ClientPlatform
{
    Unknown = 0,
    Android = 1,
    iOS = 2,
    MacOS = 3,
    Windows = 4,
    Linux = 5,
    Web = 6,
}

/// <summary>Who is allowed to see / do something.</summary>
public enum PrivacyLevel
{
    Everybody = 0,
    Contacts = 1,
    Nobody = 2,
}

/// <summary>How a phone number got attached to an account. Numbers are never SMS-verified.</summary>
public enum PhoneSource
{
    None = 0,
    /// <summary>Read from the SIM / OS by the app (Android only).</summary>
    Sim = 1,
    /// <summary>Typed in by the user.</summary>
    Manual = 2,
}

/// <summary>What a viewer is allowed to know about someone's last-seen time.</summary>
public enum LastSeenApprox
{
    Online = 0,
    Exact = 1,
    Recently = 2,
    WithinWeek = 3,
    WithinMonth = 4,
    LongAgo = 5,
}

public enum ChatType
{
    Private = 1,
    Group = 2,
    /// <summary>"Saved messages": a chat whose only member is its owner.</summary>
    Saved = 3,
}

public enum ChatRole
{
    Member = 0,
    Admin = 1,
    Owner = 2,
}

public enum MessageType
{
    Text = 0,
    Photo = 1,
    Video = 2,
    /// <summary>Several photos and/or videos in one message.</summary>
    Album = 3,
    File = 4,
    Voice = 5,
    /// <summary>Round video message ("circle").</summary>
    VideoNote = 6,
    Audio = 7,
    System = 8,
    Call = 9,
}

public enum MessageEntityType
{
    Bold = 1,
    Italic = 2,
    Underline = 3,
    Strikethrough = 4,
    Code = 5,
    Pre = 6,
    TextLink = 7,
    Url = 8,
    Mention = 9,
    Spoiler = 10,
    Email = 11,
    Phone = 12,
    Quote = 13,
}

public enum MediaKind
{
    Photo = 1,
    Video = 2,
    File = 3,
    Voice = 4,
    VideoNote = 5,
    Audio = 6,
}

public enum MediaStatus
{
    Uploading = 0,
    Processing = 1,
    Ready = 2,
    Failed = 3,
}

public enum TypingAction
{
    Cancel = 0,
    Typing = 1,
    RecordingVoice = 2,
    RecordingVideoNote = 3,
    UploadingPhoto = 4,
    UploadingVideo = 5,
    UploadingFile = 6,
}

public enum SystemEventType
{
    ChatCreated = 1,
    MembersAdded = 2,
    MemberLeft = 3,
    MemberRemoved = 4,
    MemberJoinedByLink = 5,
    TitleChanged = 6,
    PhotoChanged = 7,
    PhotoRemoved = 8,
    DescriptionChanged = 9,
    MessagePinned = 10,
    GroupCallStarted = 11,
    GroupCallEnded = 12,
}

public enum CallKind
{
    Private = 1,
    Group = 2,
}

public enum CallStatus
{
    Ringing = 0,
    Active = 1,
    Ended = 2,
    Missed = 3,
    Declined = 4,
    Cancelled = 5,
    Busy = 6,
    Failed = 7,
}

public enum CallParticipantStatus
{
    Invited = 0,
    Ringing = 1,
    Joined = 2,
    Left = 3,
    Declined = 4,
    Missed = 5,
}

public enum PushProvider
{
    None = 0,
    Fcm = 1,
    Apns = 2,
    ApnsVoip = 3,
    WebPush = 4,
}
