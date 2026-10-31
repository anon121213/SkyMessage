namespace SkyMessage.Contracts;

/// <summary>
/// Single source of truth for HTTP routes. Server actions use the template constants
/// (e.g. <c>[HttpGet(ApiRoutes.Chats.ById)]</c>, no controller-level prefix); clients use the
/// path builder methods. All routes are relative to the server base URL.
/// </summary>
public static class ApiRoutes
{
    public const string Health = "health";
    public const string ServerInfo = "api/server/info";
    /// <summary>GET ?sid=&amp;nonce= → <see cref="ServerProofDto"/> (anonymous, rate-limited).</summary>
    public const string ServerProof = "api/server/proof";
    /// <summary>
    /// GET ?size= → that many random bytes (≤ 256 KB, anonymous, rate-limited). Used to detect networks that
    /// let small handshakes through but stall real transfers (failover trigger).
    /// </summary>
    public const string ServerProbe = "api/server/probe";

    public static string ServerProofPath(Guid sessionId, string nonce) =>
        $"{ServerProof}?sid={sessionId:N}&nonce={Uri.EscapeDataString(nonce)}";
    public static string ServerProbePath(int size) => $"{ServerProbe}?size={size}";

    public static class Auth
    {
        public const string Register = "api/auth/register";
        public const string Login = "api/auth/login";
        public const string Refresh = "api/auth/refresh";
        public const string Logout = "api/auth/logout";
        /// <summary>GET ?username=</summary>
        public const string UsernameAvailable = "api/auth/username-available";
        public const string ChangePassword = "api/auth/change-password";

        public static string UsernameAvailablePath(string username) =>
            $"{UsernameAvailable}?username={Uri.EscapeDataString(username)}";
    }

    public static class Sessions
    {
        public const string Root = "api/sessions";
        public const string ById = "api/sessions/{sessionId:guid}";
        public const string Others = "api/sessions/others";
        public const string CurrentPush = "api/sessions/current/push";

        public static string ByIdPath(Guid sessionId) => $"api/sessions/{sessionId}";
    }

    public static class Users
    {
        public const string Me = "api/users/me";
        /// <summary>POST <see cref="DeleteAccountRequest"/> — deletes the account (password confirmation).</summary>
        public const string MeDelete = "api/users/me/delete";
        public const string MeAvatar = "api/users/me/avatar";
        public const string MePhone = "api/users/me/phone";
        public const string MePrivacy = "api/users/me/privacy";
        public const string ById = "api/users/{userId:guid}";
        public const string ByUsername = "api/users/by-username/{username}";
        /// <summary>POST <see cref="GetUsersRequest"/> → UserSummaryDto[].</summary>
        public const string Batch = "api/users/batch";
        /// <summary>GET ?q=&amp;limit=</summary>
        public const string Search = "api/users/search";
        public const string FindByPhones = "api/users/find-by-phones";

        public static string ByIdPath(Guid userId) => $"api/users/{userId}";
        public static string ByUsernamePath(string username) => $"api/users/by-username/{Uri.EscapeDataString(username)}";
        public static string SearchPath(string query, int limit = 30) =>
            $"{Search}?q={Uri.EscapeDataString(query)}&limit={limit}";
    }

    public static class Contacts
    {
        public const string Root = "api/contacts";
        public const string Bulk = "api/contacts/bulk";
        public const string ByUserId = "api/contacts/{userId:guid}";

        public static string ByUserIdPath(Guid userId) => $"api/contacts/{userId}";
    }

    public static class Blocks
    {
        public const string Root = "api/blocks";
        public const string ByUserId = "api/blocks/{userId:guid}";

        public static string ByUserIdPath(Guid userId) => $"api/blocks/{userId}";
    }

    public static class Chats
    {
        public const string Root = "api/chats";
        public const string ById = "api/chats/{chatId:guid}";
        public const string Details = "api/chats/{chatId:guid}/details";
        public const string Private = "api/chats/private";
        public const string Groups = "api/chats/groups";
        public const string Avatar = "api/chats/{chatId:guid}/avatar";
        public const string Members = "api/chats/{chatId:guid}/members";
        public const string Member = "api/chats/{chatId:guid}/members/{userId:guid}";
        public const string MemberRole = "api/chats/{chatId:guid}/members/{userId:guid}/role";
        public const string Leave = "api/chats/{chatId:guid}/leave";
        public const string InviteLink = "api/chats/{chatId:guid}/invite-link";
        public const string Join = "api/chats/join/{token}";
        public const string Settings = "api/chats/{chatId:guid}/settings";
        public const string Read = "api/chats/{chatId:guid}/read";
        public const string ClearHistory = "api/chats/{chatId:guid}/clear-history";
        /// <summary>GET → pinned MessageDto[] (newest first).</summary>
        public const string Pinned = "api/chats/{chatId:guid}/pinned";
        /// <summary>PUT <see cref="ReorderPinnedChatsRequest"/> → <see cref="PinnedChatsOrderDto"/> (my pinned chats' order).</summary>
        public const string PinnedOrder = "api/chats/pinned-order";
        /// <summary>PUT <see cref="SetContentProtectionRequest"/> → <see cref="ChatInfoDto"/> (no forwarding/copying/saving).</summary>
        public const string ContentProtection = "api/chats/{chatId:guid}/content-protection";

        public static string PinnedPath(Guid chatId) => $"api/chats/{chatId}/pinned";
        public static string ContentProtectionPath(Guid chatId) => $"api/chats/{chatId}/content-protection";

        public static string ByIdPath(Guid chatId) => $"api/chats/{chatId}";
        /// <summary>DELETE ?forEveryone=</summary>
        public static string DeletePath(Guid chatId, bool forEveryone) => $"api/chats/{chatId}?forEveryone={(forEveryone ? "true" : "false")}";
        public static string DetailsPath(Guid chatId) => $"api/chats/{chatId}/details";
        public static string AvatarPath(Guid chatId) => $"api/chats/{chatId}/avatar";
        public static string MembersPath(Guid chatId) => $"api/chats/{chatId}/members";
        public static string MemberPath(Guid chatId, Guid userId) => $"api/chats/{chatId}/members/{userId}";
        public static string MemberRolePath(Guid chatId, Guid userId) => $"api/chats/{chatId}/members/{userId}/role";
        public static string LeavePath(Guid chatId) => $"api/chats/{chatId}/leave";
        public static string InviteLinkPath(Guid chatId) => $"api/chats/{chatId}/invite-link";
        public static string JoinPath(string token) => $"api/chats/join/{Uri.EscapeDataString(token)}";
        public static string SettingsPath(Guid chatId) => $"api/chats/{chatId}/settings";
        public static string ReadPath(Guid chatId) => $"api/chats/{chatId}/read";
        public static string ClearHistoryPath(Guid chatId) => $"api/chats/{chatId}/clear-history";
    }

    public static class Messages
    {
        /// <summary>GET ?beforeSeq=&amp;afterSeq=&amp;aroundSeq=&amp;limit= ; POST sends a message.</summary>
        public const string Root = "api/chats/{chatId:guid}/messages";
        /// <summary>GET ?cursor= (opaque, from MessagesPageDto.SyncCursor or MessageChangesDto.NextCursor).</summary>
        public const string Changes = "api/chats/{chatId:guid}/messages/changes";
        /// <summary>GET → seqs (ascending) of my unread mentions/replies in the chat.</summary>
        public const string Mentions = "api/chats/{chatId:guid}/messages/mentions";
        public const string Forward = "api/chats/{chatId:guid}/messages/forward";
        public const string Delete = "api/chats/{chatId:guid}/messages/delete";
        public const string ById = "api/chats/{chatId:guid}/messages/{messageId:guid}";
        public const string Reaction = "api/chats/{chatId:guid}/messages/{messageId:guid}/reaction";
        public const string Pin = "api/chats/{chatId:guid}/messages/{messageId:guid}/pin";
        /// <summary>GET ?q=&amp;beforeSeq=&amp;limit=</summary>
        public const string Search = "api/chats/{chatId:guid}/messages/search";
        /// <summary>GET ?kind=&amp;beforeSeq=&amp;limit= (shared media gallery)</summary>
        public const string SharedMedia = "api/chats/{chatId:guid}/media";

        public static string RootPath(Guid chatId) => $"api/chats/{chatId}/messages";

        public static string PagePath(Guid chatId, long? beforeSeq = null, long? afterSeq = null, long? aroundSeq = null, int limit = Limits.MessagesPageDefault)
        {
            var path = $"api/chats/{chatId}/messages?limit={limit}";
            if (beforeSeq is { } b) path += $"&beforeSeq={b}";
            if (afterSeq is { } a) path += $"&afterSeq={a}";
            if (aroundSeq is { } r) path += $"&aroundSeq={r}";
            return path;
        }

        public static string ChangesPath(Guid chatId, string cursor) =>
            $"api/chats/{chatId}/messages/changes?cursor={Uri.EscapeDataString(cursor)}";
        public static string MentionsPath(Guid chatId) => $"api/chats/{chatId}/messages/mentions";
        public static string ForwardPath(Guid chatId) => $"api/chats/{chatId}/messages/forward";
        public static string DeletePath(Guid chatId) => $"api/chats/{chatId}/messages/delete";
        public static string ByIdPath(Guid chatId, Guid messageId) => $"api/chats/{chatId}/messages/{messageId}";
        public static string ReactionPath(Guid chatId, Guid messageId) => $"api/chats/{chatId}/messages/{messageId}/reaction";
        public static string PinPath(Guid chatId, Guid messageId) => $"api/chats/{chatId}/messages/{messageId}/pin";

        public static string SearchPath(Guid chatId, string query, long? beforeSeq = null, int limit = Limits.SearchResultsMax)
        {
            var path = $"api/chats/{chatId}/messages/search?q={Uri.EscapeDataString(query)}&limit={limit}";
            if (beforeSeq is { } b) path += $"&beforeSeq={b}";
            return path;
        }

        public static string SharedMediaPath(Guid chatId, SharedMediaFilter filter, long? beforeSeq = null, int limit = Limits.MessagesPageDefault)
        {
            var path = $"api/chats/{chatId}/media?kind={filter}&limit={limit}";
            if (beforeSeq is { } b) path += $"&beforeSeq={b}";
            return path;
        }
    }

    public static class Search
    {
        /// <summary>GET ?q= : chats, users and messages at once.</summary>
        public const string Global = "api/search";
        /// <summary>GET ?q=&amp;limit=</summary>
        public const string Messages = "api/search/messages";

        public static string GlobalPath(string query) => $"api/search?q={Uri.EscapeDataString(query)}";
        public static string MessagesPath(string query, int limit = Limits.SearchResultsMax) =>
            $"api/search/messages?q={Uri.EscapeDataString(query)}&limit={limit}";
    }

    public static class Media
    {
        public const string Uploads = "api/media/uploads";
        public const string Upload = "api/media/uploads/{mediaId:guid}";
        /// <summary>PUT raw bytes (application/octet-stream).</summary>
        public const string UploadChunk = "api/media/uploads/{mediaId:guid}/chunks/{index:int}";
        public const string UploadComplete = "api/media/uploads/{mediaId:guid}/complete";
        public const string ById = "api/media/{mediaId:guid}";
        /// <summary>GET ?t={mediaToken}&amp;download=1 — supports HTTP Range.</summary>
        public const string Content = "api/media/{mediaId:guid}/content";
        /// <summary>GET ?t={signature} (see <see cref="MediaSignature"/>)</summary>
        public const string Thumbnail = "api/media/{mediaId:guid}/thumbnail";

        public static string UploadPath(Guid mediaId) => $"api/media/uploads/{mediaId}";
        public static string UploadChunkPath(Guid mediaId, int index) => $"api/media/uploads/{mediaId}/chunks/{index}";
        public static string UploadCompletePath(Guid mediaId) => $"api/media/uploads/{mediaId}/complete";
        public static string ByIdPath(Guid mediaId) => $"api/media/{mediaId}";

        /// <param name="signature">From <see cref="MediaSignature.Create(string, Guid, Guid)"/>; null when an Authorization header is sent instead.</param>
        public static string ContentPath(Guid mediaId, string? signature, bool download = false)
        {
            var path = $"api/media/{mediaId}/content";
            var sep = '?';
            if (!string.IsNullOrEmpty(signature)) { path += $"{sep}t={Uri.EscapeDataString(signature)}"; sep = '&'; }
            if (download) path += $"{sep}download=1";
            return path;
        }

        public static string ThumbnailPath(Guid mediaId, string? signature) =>
            string.IsNullOrEmpty(signature)
                ? $"api/media/{mediaId}/thumbnail"
                : $"api/media/{mediaId}/thumbnail?t={Uri.EscapeDataString(signature)}";
    }

    public static class Calls
    {
        public const string Root = "api/calls";
        public const string ById = "api/calls/{callId:guid}";
        public const string Accept = "api/calls/{callId:guid}/accept";
        /// <summary>POST optional <see cref="DeclineCallRequest"/>.</summary>
        public const string Decline = "api/calls/{callId:guid}/decline";
        /// <summary>Join (group) or re-join after an app restart (private) an unfinished call I take part in.</summary>
        public const string Join = "api/calls/{callId:guid}/join";
        public const string Hangup = "api/calls/{callId:guid}/hangup";
        /// <summary>GET ?chatId= → active call in a chat (204 when none).</summary>
        public const string Active = "api/calls/active";
        /// <summary>GET ?before=&amp;limit=</summary>
        public const string History = "api/calls/history";
        public const string LiveKitWebhook = "api/livekit/webhook";

        public static string ByIdPath(Guid callId) => $"api/calls/{callId}";
        public static string AcceptPath(Guid callId) => $"api/calls/{callId}/accept";
        public static string DeclinePath(Guid callId) => $"api/calls/{callId}/decline";
        public static string JoinPath(Guid callId) => $"api/calls/{callId}/join";
        public static string HangupPath(Guid callId) => $"api/calls/{callId}/hangup";
        public static string ActivePath(Guid chatId) => $"api/calls/active?chatId={chatId}";

        public static string HistoryPath(DateTimeOffset? before = null, int limit = 50) =>
            before is { } b
                ? $"api/calls/history?limit={limit}&before={Uri.EscapeDataString(b.UtcDateTime.ToString("O"))}"
                : $"api/calls/history?limit={limit}";
    }
}

/// <summary>
/// Real-time hub and reverse-proxied endpoints. Server maps them at these paths; clients resolve them
/// relative to their server base URI (strip the leading '/'), so deployments under a path prefix work.
/// </summary>
public static class HubRoutes
{
    public const string Realtime = "/hubs/realtime";
    /// <summary>LiveKit signaling is proxied by the API under this path (WebSocket).</summary>
    public const string LiveKitProxy = "/livekit";
}

public enum DeepLinkKind
{
    /// <summary>Value = username.</summary>
    User = 1,
    /// <summary>Value = group invite token.</summary>
    Join = 2,
    /// <summary>Value = chat id (used by notification taps).</summary>
    Chat = 3,
}

public sealed record DeepLinkTarget(DeepLinkKind Kind, string Value);

/// <summary>
/// Links understood by the web client and the apps, in both forms:
/// <c>skymessage://u/{username}</c>, <c>skymessage://join/{token}</c>, <c>skymessage://chat/{id}</c> and
/// <c>https://{server}/u/{username}</c>, <c>/join/{token}</c>, <c>/chat/{id}</c>.
/// </summary>
public static class DeepLinks
{
    public const string AppScheme = "skymessage";

    public static string UserProfilePath(string username) => $"/u/{username}";
    public static string JoinChatPath(string token) => $"/join/{token}";
    public static string ChatPath(Guid chatId) => $"/chat/{chatId}";
    public static Uri AppChatUri(Guid chatId) => new($"{AppScheme}://chat/{chatId}");

    public static bool TryParse(Uri uri, out DeepLinkTarget? target)
    {
        target = null;
        IEnumerable<string> segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (string.Equals(uri.Scheme, AppScheme, StringComparison.OrdinalIgnoreCase))
            segments = segments.Prepend(uri.Host); // skymessage://u/name → host "u", path "/name"
        else if (uri.Scheme is not ("http" or "https"))
            return false;

        var parts = segments.Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length != 2 || parts[1].Length == 0) return false;

        switch (parts[0].ToLowerInvariant())
        {
            case "u":
                target = new DeepLinkTarget(DeepLinkKind.User, parts[1].TrimStart('@'));
                return true;
            case "join":
                target = new DeepLinkTarget(DeepLinkKind.Join, parts[1]);
                return true;
            case "chat" when Guid.TryParse(parts[1], out var chatId):
                target = new DeepLinkTarget(DeepLinkKind.Chat, chatId.ToString());
                return true;
            default:
                return false;
        }
    }
}

public enum SharedMediaFilter
{
    Media = 1,
    Files = 2,
    Voice = 3,
    Links = 4,
    VideoNotes = 5,
}
