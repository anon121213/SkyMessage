namespace SkyMessage.Contracts;

/// <summary>
/// Machine-readable error codes. The API returns RFC 7807 problem details with an extra
/// <c>code</c> property holding one of these values; clients map codes to localized text.
/// </summary>
public static class ErrorCodes
{
    public const string Validation = "validation_failed";
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string Unauthorized = "unauthorized";
    public const string Conflict = "conflict";
    public const string RateLimited = "rate_limited";
    public const string Internal = "internal_error";

    // Auth
    public const string InvalidCredentials = "invalid_credentials";
    public const string UsernameTaken = "username_taken";
    public const string UsernameInvalid = "username_invalid";
    public const string PasswordTooWeak = "password_too_weak";
    public const string RegistrationClosed = "registration_closed";
    public const string RefreshTokenInvalid = "refresh_token_invalid";
    public const string SessionRevoked = "session_revoked";
    public const string WrongPassword = "wrong_password";

    // Users
    public const string UserNotFound = "user_not_found";
    public const string PhoneInvalid = "phone_invalid";
    public const string PhoneTaken = "phone_taken";
    public const string UserBlocked = "user_blocked";
    public const string PrivacyRestricted = "privacy_restricted";
    public const string CannotTargetSelf = "cannot_target_self";

    // Chats
    public const string ChatNotFound = "chat_not_found";
    public const string NotChatMember = "not_chat_member";
    public const string NotChatAdmin = "not_chat_admin";
    public const string ChatMemberLimit = "chat_member_limit";
    public const string InviteInvalid = "invite_invalid";
    public const string NotAGroup = "not_a_group";

    // Messages
    public const string MessageNotFound = "message_not_found";
    public const string MessageEmpty = "message_empty";
    public const string MessageEditExpired = "message_edit_expired";
    public const string MessageNotEditable = "message_not_editable";
    public const string SendForbidden = "send_forbidden";
    /// <summary>The source chat has content protection on: its messages cannot be forwarded.</summary>
    public const string ForwardRestricted = "forward_restricted";

    // Media
    public const string MediaNotFound = "media_not_found";
    public const string MediaNotReady = "media_not_ready";
    public const string MediaTooLarge = "media_too_large";
    public const string MediaUnsupported = "media_unsupported";
    public const string ChunkInvalid = "chunk_invalid";
    public const string UploadIncomplete = "upload_incomplete";

    // Calls
    public const string CallNotFound = "call_not_found";
    public const string CallBusy = "call_busy";
    public const string CallEnded = "call_ended";
    public const string CallsDisabled = "calls_disabled";
}
