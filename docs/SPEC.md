# SkyMessage — implementation spec (v1)

Private, self-hosted messenger with the feature set of a modern messenger (1:1 and group chats, media,
voice messages, round video messages, audio/video calls, rich settings). Built for a family spread
between Russia and Thailand: it must keep working on restrictive, lossy networks while staying legal.

The **source of truth for every shared shape is code**, not this document:

| What | Where |
|---|---|
| DTOs, enums, limits, error codes, routes, deep links, media signatures, realtime interfaces | `src/Shared/SkyMessage.Contracts/**` |
| Entities | `src/Server/SkyMessage.Domain/Entities/**` |
| EF configuration (indexes, jsonb, keys) | `src/Server/SkyMessage.Infrastructure/Persistence/**` |
| Cross-feature server interfaces + helpers (ChatSequence, SyncCursor, options) | `src/Server/SkyMessage.Application/Common/**` |
| Client service/store/platform interfaces | `src/Client/SkyMessage.Client.Core/{Services,Stores,Platform,Settings}/**` |

Read those files (doc comments are normative) before writing code. This spec explains behavior, rules
and ownership.

---

## 1. Principles (non-negotiable)

1. **No third-party IP.** Never use the name, logo, icons, stickers, sounds, wallpapers or branding of
   Telegram or any other messenger, and **never port, translate or paraphrase code or layout algorithms
   from any messenger's client source** (several are GPL). Describe and build UX from first principles.
   Our brand: **SkyMessage**, default accent violet `#6C5CE7`, logo = a soft gradient cloud (violet → sky
   blue) without a speech-bubble tail, our own gradient/pattern wallpapers, sounds synthesized with
   WebAudio. Icons: Lucide (ISC) from the `lucide-static` npm package, license shipped in `wwwroot/licenses/`.
2. **License policy.** Only MIT / Apache-2.0 / BSD / ISC / OFL dependencies. Do **not** add MediatR,
   AutoMapper, FluentAssertions, MassTransit, ImageSharp, Moq. Exception: Google Play services / Firebase
   Android SDKs (free, proprietary) are allowed for platform integration (phone-number hint now, push later).
   FFmpeg is invoked as an external process on the server only (never linked, never shipped in apps).
   Every shipped package's license/notice goes into the About › Licenses page.
3. **Network resilience (legal).** One HTTPS origin on 443 for everything: REST, SignalR
   (WebSockets → SSE → long polling), media, LiveKit signaling (reverse-proxied at `/livekit`). Clients
   keep a list of server addresses of the same deployment (failover by `InstanceId`), a user-editable
   server address and an HTTP/SOCKS5 proxy. Offline-first: cached reads, persisted outbox, automatic
   catch-up. No VPN functionality, nothing that gives access to third-party resources.
4. **Security.** TLS in production; tokens never in logs; message text never logged. Server-side storage
   is readable by the server (no E2E in v1) but nothing may block adding E2E "secret chats" later.
   Untrusted content (message text, links, file names) is never rendered as HTML.
5. **Russian first.** Every UI string is localized (ru + en); Russian when the OS language is Russian or unknown.
6. **Everything in PostgreSQL**, including media bytes (chunked bytea, AES-256-GCM at rest when
   `Storage:MasterKey` is set).

## 2. Architecture

```
src/Shared/SkyMessage.Contracts      net10.0   shared kernel: DTOs, enums, limits, routes, hub interfaces, MediaSignature, DeepLinks
src/Server/SkyMessage.Domain         net10.0   entities (anemic, EF-friendly)
src/Server/SkyMessage.Application    net10.0   use-case services, validators, cross-feature interfaces
src/Server/SkyMessage.Infrastructure net10.0   EF Core/Npgsql, JWT, hashing, media storage/processing, LiveKit, background services
src/Server/SkyMessage.Api            net10.0   ASP.NET Core host: controllers, SignalR hub, YARP /livekit proxy, serves the web client
src/Client/SkyMessage.Client.Core    net10.0   client logic: API client, realtime, stores, outbox, upload/download, settings (UI-free, WASM-safe)
src/Client/SkyMessage.Client.UI      net10.0   Razor class library: every screen, component, CSS, JS, localization
src/Client/SkyMessage.App            net10.0-android;ios;maccatalyst  MAUI Blazor Hybrid host + native platform services
src/Client/SkyMessage.Web            net10.0   Blazor WebAssembly host (served by the API at "/")
tests/SkyMessage.UnitTests           unit tests (each feature in its own folder)
tests/SkyMessage.Api.IntegrationTests  WebApplicationFactory + Testcontainers PostgreSQL (ApiFactory exists)
docker-compose.yml                   local dev: postgres + livekit (+ api)
```

Dependency rule: Api → Infrastructure → Application → Domain → Contracts. Controllers are thin: bind,
call an Application service, return a DTO. Application services use `IAppDbContext` directly (no
repository layer), throw `AppException` subclasses for expected errors and publish realtime events via
`IRealtimeNotifier`. Infrastructure implements technical interfaces.

Feature registration hooks (stubs exist): Application `AddIdentityFeature/AddMessagingFeature/
AddMediaFeature/AddCallsFeature`; Infrastructure `Add{Identity,Messaging,Media,Calls}Infrastructure`;
Api `AddLiveKitProxy/MapLiveKitProxy`. Controllers are discovered automatically.

## 3. Conventions

- C# latest, nullable, file-scoped namespaces, 4-space indent. Comments only where they explain *why*.
  Match the style of the foundation files.
- IDs: `Guid.CreateVersion7()`. Times: `IClock.UtcNow` (UTC, truncated to microseconds = PostgreSQL
  precision, so values round-trip exactly). Never `DateTimeOffset.UtcNow` in services.
- JSON: `SkyJson` everywhere (camelCase, enums as strings).
- Routes: actions use `ApiRoutes.*` template constants on the action attribute (no controller-level
  `[Route]`): `[HttpGet(ApiRoutes.Chats.ById)]`. Clients use the `*Path(...)` builders.
- Auth: the fallback policy requires an authenticated user. **Anonymous endpoints (exhaustive):**
  register, login, refresh, username-available, server/info, health, media content/thumbnail (signature
  or bearer), LiveKit webhook (own signature). JWT `sub` = user id `ToString()`, `sid` = session id; the
  session must belong to `sub` (checked on every request via `ISessionValidator`).
- Errors: throw `NotFoundException(ErrorCodes.ChatNotFound)` etc. → problem details `{status, title,
  code, errors?}`. Validators: FluentValidation `<Request>Validator`, `internal sealed`, auto-run.
- Never leak existence across authorization boundaries: anything the caller can't see is 404.
- Reads: project to DTOs, `AsNoTracking()`, batch-load (no N+1), `MediaMapping.Projection` for `MediaDto`.
- **Transactions & events**: publish realtime events only after the transaction committed (or after
  `SaveChangesAsync` when there is no explicit transaction). Seq allocation and chat-aggregate updates use
  `ChatSequence` (see its doc). Counters (`MemberCount`) change via `ExecuteUpdateAsync` increments inside
  the transaction. Expected races on unique indexes (duplicate ClientMessageId, private chat key,
  username) are handled in the service: roll back, `ChangeTracker.Clear()`, re-read and return the existing
  row (or the specific error code).
- Realtime payloads are viewer-independent (`MessageDto`, `ChatInfoDto`, events); viewer-dependent data
  (`ChatSummaryDto`, `UserSummaryDto`) is fetched via REST when an event says so.
- **Migrations**: never run `dotnet ef migrations add`. While the repo has no migrations the API (and tests)
  create the schema with `EnsureCreated` (`PersistenceSetup.EnsureCreatedWithExtrasAsync`). The lead
  generates the single initial migration after integration.
- Logging: structured, ids only. Async with `CancellationToken` everywhere.
- Tests: xUnit v3 + Shouldly + NSubstitute. Run with `dotnet test --project <csproj>` (Microsoft.Testing.Platform).

## 4. Ownership (parallel work)

Several agents work at the same time. **Only edit files you own.** A change in a shared/foreign file must
be minimal and *additive*: re-read the file right before editing and list the change in your report.
Never rename or remove anything in shared files.

| Owner | Owns |
|---|---|
| lead | Contracts, Domain (as a whole), `Application/Common/**`, `Application/DependencyInjection.cs`, `Infrastructure/Persistence/**` (incl. migrations), `Infrastructure/DependencyInjection.cs`, `Api/Program.cs`, `Api/ApiSetup.cs`, `Api/Infrastructure/**`, Directory.*.props, docker-compose, Dockerfile, `tests/**/ApiFactory.cs`, `tests/**/SmokeTests.cs` |
| backend-identity | `Application/Identity/**`, `Infrastructure/Identity/**`, `Infrastructure/Push/**`, `Api/Hubs/**`, `Api/Controllers/{Auth,Sessions,Users,Contacts,Blocks,Server}Controller.cs`, `tests/SkyMessage.UnitTests/Identity/**`; may edit `Domain/Entities/Users.cs`, `Configurations/UserConfigurations.cs` |
| backend-messaging | `Application/Messaging/**`, `Infrastructure/Messaging/**`, `Api/Controllers/{Chats,Messages,Search}Controller.cs`, `tests/SkyMessage.UnitTests/Messaging/**`; may edit `Domain/Entities/{Chats,Messages}.cs`, `Configurations/{Chat,Message}Configurations.cs` |
| backend-media | `Application/MediaFiles/**`, `Infrastructure/MediaFiles/**`, `Api/Controllers/MediaController.cs`, `tests/SkyMessage.UnitTests/Media/**`; may edit `Domain/Entities/Media.cs`, `Configurations/MediaConfigurations.cs` |
| backend-calls | `Application/Calls/**`, `Infrastructure/Calls/**`, `Api/Calls/**`, `Api/Controllers/{Calls,LiveKitWebhook}Controller.cs`, `docker/livekit/**`, `tests/SkyMessage.UnitTests/Calls/**`; may edit `Domain/Entities/Calls.cs`, `Configurations/CallConfigurations.cs` |
| client-core | `src/Client/SkyMessage.Client.Core/**` (interface files: additive changes only, reported), `tests/SkyMessage.Client.Core.Tests/**` |
| ui-kit | `Client.UI`: `Routes.razor`, `UiSetup.cs`, `_Imports.razor`, `Shell/**`, `Kit/**` (incl. `Kit/Emoji/**`), `Services/**`, `Features/{Auth,ChatList,Contacts,Profile,Groups,Search}/**`, `wwwroot/{css,img,licenses}/**`, `wwwroot/js/{app,boot,sounds}.js`, `Localization/*/{common,errors,auth,chatlist,contacts,profile,groups,search}.json`; creates the stubs listed in §8.2 |
| ui-chat | `Client.UI/Features/Chat/**`, `Client.UI/Features/Media/**`, `wwwroot/js/{chat,recorder,player,viewer}.js`, `Localization/*/{chat,media}.json` |
| ui-settings-calls | `Client.UI/Features/{Settings,Calls}/**`, `wwwroot/js/{devices,calls}.js`, `wwwroot/lib/livekit/**`, `Localization/*/{settings,calls}.json` |
| app-platform | `src/Client/SkyMessage.App/**`, `src/Client/SkyMessage.Web/**`, `Client.UI/Localization/*/platform.json` |

## 5. Building while others work

- Never build the whole solution. Build only your project, with a private artifacts folder so concurrent
  builds never lock each other's files:
  - backend: `dotnet build src/Server/SkyMessage.Api/SkyMessage.Api.csproj -p:SkipWebClient=true --artifacts-path artifacts/<agent>`
  - tests: `dotnet test` ignores `--artifacts-path` with Microsoft.Testing.Platform (runs 0 tests). Instead build
    with `dotnet build tests/<Proj>/<Proj>.csproj --artifacts-path artifacts/<agent>-tests` and run the test app
    directly: `dotnet artifacts/<agent>-tests/bin/<Proj>/debug/<Proj>.dll` (filter: `-- --filter-class <Name>`).
  - client-core: `dotnet build src/Client/SkyMessage.Client.Core/SkyMessage.Client.Core.csproj --artifacts-path artifacts/client-core`
  - ui: `dotnet build src/Client/SkyMessage.Client.UI/SkyMessage.Client.UI.csproj --artifacts-path artifacts/<agent>`
  - app-platform: `dotnet build src/Client/SkyMessage.App/SkyMessage.App.csproj -f net10.0-android -p:AndroidSdkDirectory=$HOME/Library/Android/sdk "-p:JavaSdkDirectory=/Applications/Android Studio.app/Contents/jbr/Contents/Home" --artifacts-path artifacts/app-platform` and `dotnet build src/Client/SkyMessage.Web/SkyMessage.Web.csproj --artifacts-path artifacts/app-platform-web`
- SDK: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`.
- A build error in a file you don't own usually means another agent is mid-edit: continue with other work
  and rebuild later; never "fix" someone else's file. Write each file completely in one go and keep your
  code compiling (create referenced types before their users).
- Do not start the API, emulators or long-running servers (the lead integrates). Docker is available for
  Testcontainers-based tests.
- Finish only when your project builds with **0 errors**. Report warnings you introduced.

## 6. Backend

### 6.1 Common rules

- Membership: a user sees a chat iff a `ChatMember` row exists (lists also require `IsHidden = false`).
  Visible messages: `Seq > member.ClearedUpToSeq`, not in `HiddenMessages` for that user. Pages exclude
  tombstones (`DeletedAt != null`); `/changes` includes them.
- Roles: Owner ⊃ Admin ⊃ Member. Admins/owner bypass `GroupPermissions`. Only the owner manages admins.
- Every message change bumps `Message.UpdatedAt` (= `IClock.UtcNow` in the same transaction).
- Realtime recipients for chat events = all members' user ids (incl. the actor's other devices; clients
  dedupe by id / ClientMessageId). Blocked users never receive Typing/Presence of the blocker.

### 6.2 Identity (backend-identity)

**Registration** (anonymous, `Auth` rate limit): `Server:RegistrationMode` — Open (default, the owner's
choice), InviteCode (`RegisterRequest.InviteCode == Server:RegistrationCode`, else 403
`registration_closed`), Closed. Validate username (`Limits.UsernamePattern`; reserved: admin, support,
root, system, skymessage, telegram, whatsapp, deleted… → `username_invalid`), password length, display name
(trimmed 1..64). Unique `UsernameNormalized` → 409 `username_taken` (also on the race). Create User, the
**Saved Messages chat** (`Chat{Type=Saved, PrivateKey=Chat.SavedKeyFor(id), MemberCount=1}` + Owner member)
and a Session. Return `AuthResponse`.

**Login** (anonymous, `Auth`): username (normalized, leading `@` ok) or phone (starts with `+` or is mostly
digits; parsed with `DefaultRegion`, then the RU fallback). Wrong user/password → 401
`invalid_credentials` (identical). `PasswordHasher<User>`; rehash on `SuccessRehashNeeded`. Lockout: 10
failures per login per 15 min → 429.

**Tokens**: access JWT HS256 (`sub`, `sid`, `name`, `jti`, `rtb`), `AccessTokenMinutes`. Refresh token at
sign-in: 32 random bytes base64url, stored as SHA-256 hex, sliding `RefreshTokenDays`. **Media key**: 32 random
bytes per session (`Session.MediaKey`), returned as `AuthResponse.MediaKey` on login/register/refresh.
`POST api/auth/refresh` (anonymous): by hash → rotate to the **successor** `HMAC-SHA256(key derived from
Jwt:SigningKey, "{sid:N}:{token}")` (Previous = current, `RefreshTokenRotatedAt = now`, `RefreshTokenConfirmedAt
= null`). Access tokens carry `rtb` = the first 16 hex chars of their refresh token's hash; the first
authenticated request with an `rtb` matching the current hash stamps `RefreshTokenConfirmedAt` (the client got
that response). A match on `PreviousRefreshTokenHash` is a lost-response retry while unconfirmed (any time) or
within 2 minutes of rotation: the same successor is returned again (idempotent; concurrent refreshes get one
token). After confirmation and the 2 minutes it is theft → revoke the session, 401 `refresh_token_invalid`.
Logout revokes the current session. `GET api/auth/username-available` (anonymous, `UsernameCheck` limit).
Change password: wrong current → 400 `wrong_password`; optionally revoke other sessions.
**Delete account** `POST api/users/me/delete {password}`: tombstone (`IsDeleted`, `DeletedAt`, username →
`deleted_{id:N}`, display name "Deleted Account", clear bio/phone/avatar), revoke all sessions, leave groups
(ownership transfer rules), keep messages (shown as from a deleted account).

**Sessions**: `GET api/sessions`, `DELETE api/sessions/{id}`, `DELETE api/sessions/others`,
`PUT api/sessions/current/push` (stored; sending pushes is v2 — register a logging no-op
`IPushNotificationService`). Revoking: `RevokedAt`, evict validator caches, send `SessionRevoked` to
`notifier.Session(id)`, then **abort that session's hub connections** (the hub keeps a singleton
sessionId → connection contexts map). Hub endpoints close on token expiry anyway.

**ISessionValidator**: `IsActiveAsync(sid, uid)` (active and owned by uid; cached 30 s, evicted on
revoke); `ValidateMediaSignatureAsync(token, mediaId)` → parse with `MediaSignature.TryParse`, load the
session's key (cached 5 min, evicted on revoke), `MediaSignature.Verify`.

**Users**: `GET/PATCH api/users/me` (MeDto incl. `SavedMessagesChatId`), avatar `PUT/DELETE` (caller's own
`Photo`, `Ready`), phone `PUT/DELETE` (libphonenumber; region = `DefaultRegion` ?? caller's phone region ??
"RU"; valid; E.164; taken → 409 `phone_taken`; optional FindByPhone update), privacy `GET/PUT`.
Any profile/privacy/phone/avatar change sends `UserChanged` to related users **and the caller's own user**.
`GET api/users/{id}`, `GET api/users/by-username/{username}` → `UserProfileDto` (viewer-dependent).
`POST api/users/batch {userIds ≤ 200}` → `UserSummaryDto[]` (unknown ids skipped).
`GET api/users/search?q=` (`Search` limit): q ≥ 2 after stripping `@`. Matches: contacts and members of my
chats by display/contact name `ILIKE %q%` or username prefix; **everyone else only by username prefix
with q ≥ `Limits.GlobalUsernameSearchMinLength`** (no enumeration by display name). Excludes self, deleted
users and users who blocked me. Contacts first, then exact username match. Limit ≤ 50.
`POST api/users/find-by-phones {phoneNumbers ≤1000, defaultRegion}` (`PhoneLookup` limit): users whose
`FindByPhone` allows me, excluding blocks; echo submitted strings.

**Contacts/blocks**: `GET/POST api/contacts`, `POST api/contacts/bulk`, `PATCH/DELETE api/contacts/{userId}`
(idempotent, no self → `cannot_target_self`, `IsMutual`), `GET api/blocks`, `PUT/DELETE api/blocks/{userId}`;
both send `ContactsChanged` to the caller's user (blocks also `UserChanged` to both sides).

**Privacy** (`IPrivacyService`): blocks first — if the owner blocked the viewer every level is Nobody.
`CanMessage(from,to)`: not blocked AND (`to.Messages == Everybody` OR `to` has `from` in contacts OR `to`
already sent a message in their private chat); Nobody ≈ Contacts. `CanCall` per `to.Calls`.
`CanAddToGroup` per `GroupInvites` (Nobody ≈ Contacts). `CanSeeProfilePhoto` per `ProfilePhoto`.

**Presence**: online = any *active* connection (`SetActive`; connections start active). Online↔offline
transitions update `LastSeenAt` and send `PresenceChanged` to related users with the per-viewer
`PresenceDto` (skip viewers blocked by the user). Debounce offline by 10 s. Visible & online →
`{IsOnline, Approx=Online}`; visible & offline → `{LastSeenAt, Exact}`; hidden → never online,
`LastSeenAt=null`, Approx by recency (≤3 d Recently, ≤7 d WithinWeek, ≤30 d WithinMonth, else LongAgo).

**Hub** `RealtimeHub`: on connect join `SessionGroup(sid)`, register in the session→connections map,
presence connect, touch `Session.LastActiveAt` (≤ 1/5 min); on disconnect the reverse. `SetActive(bool)`.
`SendTyping(chatId, action)`: membership check (cached 60 s), ≥ 300 ms throttle per connection, `Typing`
to other members (minus users who blocked the sender).

**Server info** `GET api/server/info` (anonymous): `InstanceId` (from `ServerProperties`, generated on first
start), name, version, api version 1, `MinClientVersion`, `PublicBaseUrl`, `RegistrationMode`, upload
limits, `CallsEnabled`, server time, features, `MirrorUrls` (`Server:MirrorUrls`).
`GET api/server/proof?sid=&nonce=` (anonymous, rate-limited): `ServerProofDto{InstanceId, Proof =
MediaSignature.CreateProof(session.MediaKey, nonce, Request.Host.Host)}` for an active session, else 404.
`GET api/server/probe?size=` (anonymous, rate-limited, ≤ 256 KB of random bytes, `no-store`).

### 6.3 Messaging (backend-messaging)

Chats:
- `GET api/chats` → visible `ChatSummaryDto[]`, batch-built: members, last visible message (+
  `LastMessageSender`), unread (`seq > max(LastReadSeq, ClearedUpToSeq)`, not mine, not deleted, not hidden),
  `UnreadMentionCount`/`FirstUnreadMentionSeq` (MessageMentions over the same window), `OthersLastReadSeq`,
  `ClearedUpToSeq`, peers via `IUserSummaryService` (private avatar via `CanSeeProfilePhoto`), active call.
- `GET api/chats/{id}`, `GET api/chats/{id}/details` (members incl. `LastReadSeq`, invite token for
  admins, pinned newest first), `GET api/chats/{id}/pinned`.
- `POST api/chats/private {userId}`: self → Saved; existing → unhide for caller; else `CanMessage`
  (403 `privacy_restricted`/`user_blocked`) and create with the peer's row `IsHidden = true`.
- `POST api/chats/groups` → `CreateGroupResultDto`: title 1..128, avatar must be the caller's own
  `Ready` Photo, members via `CanAddToGroup` (failures returned), creator Owner, system `ChatCreated`.
- `PATCH api/chats/{id}`, avatar `PUT/DELETE` (caller's own Photo), system messages, `ChatUpdated`.
- Members: add (AddMembers permission or admin; **banned users: only admins can add, which lifts the ban**),
  remove (admin; owner removes admins; creates a `ChatBan`; then calls every `IChatMembershipObserver`),
  role (owner; Owner = transfer), leave (ownership passes to the oldest admin, else oldest member; last
  member out deletes the group). `ChatAdded` / `ChatRemoved` / `ChatMembersChanged` + `ChatUpdated`.
- Invite links (admins, or members when AddMembers is allowed; token = 16 random bytes base64url):
  `POST/DELETE api/chats/{id}/invite-link`, `GET/POST api/chats/join/{token}` (authenticated; banned users
  and invalid tokens → 404 `invite_invalid`).
- `PUT .../settings` → `ChatSettingsChanged` to the caller. `POST .../read {upToSeq}`: clamp, only up,
  `MessagesRead` to members. `POST .../clear-history`, `DELETE api/chats/{id}?forEveryone=`.

Messages:
- `GET .../messages?beforeSeq|afterSeq|aroundSeq&limit` → `MessagesPageDto` (ascending, default 50/max 100,
  `HasMore*` via limit+1, `Users`, `MyReactions`, `SyncCursor = SyncCursor.ForNow(now)`).
- `GET .../messages/changes?cursor=` → `MessageChangesDto`: items with `(UpdatedAt, Id) > cursor` ordered by
  that tuple, ≤ 500; `HiddenMessageIds` = my hidden rows created after cursor time; `ClearedUpToSeq`;
  `NextCursor` = last item tuple when `HasMore`, else `Max(cursor, SyncCursor.ForNow(now))`. Invalid cursor →
  400 `validation_failed`.
- `GET .../messages/mentions` → my unread mention seqs (ascending).
- `POST .../messages`: membership; group permissions for non-admins; private: `CanMessage` vs peer; Saved
  always. `ClientMessageId != Guid.Empty`. Text ≤ 4096; whitespace-only without media → `message_empty`.
  Entities within bounds; `TextLink.Url` absolute http/https/mailto/tel ≤ 2048 chars. ≤ 10 media owned by
  the sender and `Ready`; Voice/VideoNote alone; Photo/Video mix (Album); else Files. Type inference: no
  media → Text; 1 Photo/Video → Photo/Video; several → Album; File/Audio/Voice/VideoNote. Reply target visible
  in the same chat. Idempotent on (SenderId, ClientMessageId), race-safe. Transaction: `ChatSequence.NextAsync`,
  insert message + attachments + `MessageMention` rows (Mention entities with UserId; `@username` text
  resolved to members; reply to another member's message), `Chat.LastMessageId`, unhide hidden members
  (they get `ChatAdded`), sender `LastReadSeq = seq`, commit → `MessageCreated`, push.
- Forward `POST .../messages/forward` (target chat in route; `ClientMessageIds` parallel to `MessageIds` for
  idempotency): copy text/entities/attachments with `ForwardInfo` (keep the original forward info); skip
  system/call messages.
- Edit (own, ≤ `EditWindowHours`, not system/call) → `EditedAt`, mentions re-indexed → `MessageUpdated`.
- Delete `POST .../messages/delete`: forEveryone — own always; private chats any message; groups others'
  only for admins → tombstone (clear text/entities/attachments/reactions/mentions/pin), fix
  `Chat.LastMessageId` under `ChatSequence.LockChatAsync` → `MessagesDeleted{ForEveryone=true}`. For me →
  `HiddenMessages` → `MessagesDeleted{ForEveryone=false}` to the caller.
- Reactions `PUT/DELETE .../reaction` (one per user, ≤ 16 chars) → `MessageUpdated`. Pins `PUT/DELETE .../pin`
  (groups: admin or PinMessages) → system `MessagePinned`, `PinnedMessagesChanged`, `MessageUpdated`.
- Search: `GET .../messages/search?q=` → `MessagesPageDto` (ILIKE with escaped `%_`, newest first),
  `GET api/search/messages?q=`, `GET api/search?q=` (`GlobalSearchResultDto`; users like users/search).
  Shared media `GET api/chats/{id}/media?kind=`.
- `ISystemMessageService`: own transaction, rejects an ambient one, publishes after commit.
- One batched DTO loader (attachments via projection, reactions ≤ 100 ids, reply previews, pinned flags).

### 6.4 Media (backend-media)

- `POST api/media/uploads`: size 1..`MaxUploadBytes`, sanitized file name, content type per kind (Photo:
  jpeg/png/webp/gif/heic/heif; Video/VideoNote: video/*; Voice: audio/* or video/webm; Audio: audio/*; File:
  any) else `media_unsupported`; `ChunkCount = ceil(size/ChunkSize)`; wrapped data key when encrypting.
- Chunks `PUT .../chunks/{index}` (owner, `Uploading`, exact length else `chunk_invalid`, upsert),
  `GET api/media/uploads/{id}` (owner only; others 404), `POST .../complete` (all chunks or
  `upload_incomplete`) → `Processing`, enqueue.
- Encryption: AES-256-GCM. Chunk = nonce(12) | ciphertext | tag(16), AAD = mediaId (16 bytes, big-endian
  Guid) ‖ chunk index (int32 big-endian). Data key wrapped with the master key, AAD = mediaId.
- Worker (`BackgroundService`, `Channel<Guid>`, `MaxConcurrency`, re-enqueue `Processing` on start): assemble
  to temp, verify optional SHA-256, then:
  - Photo: SkiaSharp with EXIF orientation. JPEG/PNG/WebP within `PhotoMaxDimension` are kept as uploaded
    (clients strip metadata); larger or other formats → JPEG q85 (metadata stripped). GIF as is. HEIC →
    ffmpeg → JPEG, else Failed. Thumbnail ≤ 480 JPEG q75 (`MediaThumbnail`), mini ≈ 24 px (`MiniThumbnail`).
  - Video: ffprobe; H.264/AAC MP4 within `VideoMaxDimension` → remux `+faststart`; else transcode (CRF
    `VideoCrf`, yuv420p, AAC 128k); poster thumbnail.
  - VideoNote: always transcode to a centered square `VideoNoteSize`², ≤ 60 s, H.264 + AAC 64k mono.
  - Voice: AAC-LC 48 kbps mono 48 kHz `.m4a` (`audio/mp4`), duration, waveform (100 peak buckets 0..255).
  - Audio: duration. File: nothing (original bytes are sacred: "send without compression" uploads Files).
  Replace chunks, update metadata, `Ready`, then `MediaProcessed` to the owner (also on `Failed`). Missing
  ffmpeg → store as-is and log.
- `GET api/media/{id}/content?t=&download=1`, `.../thumbnail?t=` (anonymous endpoints): require a bearer
  user or a valid `?t=` signature for that media id. Access: owner; or current `users.avatar_media_id` of
  a user whose photo the viewer may see (`CanSeeProfilePhoto`); or current avatar of a chat the viewer is
  a member of; or attached to a non-deleted message in a chat where the viewer is a member and the message
  is visible to them. Positive checks cached 60 s. Seekable chunk-backed stream with `EnableRangeProcessing`,
  `ETag`, `Cache-Control: private, max-age=31536000, immutable`, `nosniff`, `CSP: sandbox`; Files and
  dangerous types (html, svg, xml, js) → `attachment` (+ octet-stream when dangerous).
- `GET api/media/{id}` → `MediaDto` (same access rule).
- Cleanup hourly: `Uploading` older than `IncompleteUploadTtlHours`; unreferenced `Ready` media older than 7 days.

### 6.5 Calls (backend-calls)

- YARP proxies `/livekit/**` → `LiveKit:InternalUrl` (WebSockets, `PathRemovePrefix("/livekit")`, route
  `AuthorizationPolicy = "anonymous"`, long activity timeout). Verify against the vendored
  livekit-client that a path-prefixed URL produces `/livekit/rtc...` requests (incl. `/rtc/validate`).
- Token: HS256 with `ApiSecret`, `iss=ApiKey`, **identity `sub = "{userId:N}.{sessionId:N}"`** (per device),
  `name`, `nbf/exp` (`TokenTtlMinutes` = 15), `video={room, roomJoin, canPublish, canSubscribe,
  canPublishData}` (JSON object), `metadata` JSON `{userId, avatarMediaId}`. Room `call-{id:N}`.
- `POST api/calls`: member; disabled → 503 `calls_disabled`. Private: `CanCall` (403); my own unfinished
  call in this chat → its join info; peer busy (unfinished call elsewhere) → 409 `call_busy`; create
  Ringing (caller Joined, callee Ringing), `IncomingCall` to callee, `CallUpdated` to caller's user, push.
  Group: active → join; else Active call, others Invited, system `GroupCallStarted`, `IncomingCall`,
  `ChatUpdated(ActiveCallId)`.
- `accept` (callee: Active, `AnsweredAt`), `decline {busy?}` (private: Declined or Busy; group: my
  participant only), `join` (group, or re-join an unfinished private call I'm in), `hangup` (private:
  Ringing by caller → Cancelled, by callee → Declined, Active → Ended; group: leave, last out ends +
  `GroupCallEnded`). Every change → `CallUpdated` to participants (group: all members). Finished private
  calls → `ISystemMessageService.PostCallLogAsync` (after commit). Best-effort Twirp `DeleteRoom` on end.
- `IChatMembershipObserver`: a removed member is marked Left in an active group call and kicked via Twirp
  `RemoveParticipant` (all their identities).
- Background service (5 s tick): Ringing older than `CallRingTimeoutSeconds` → Missed (+ log, push). Every
  30 s reconcile: Active calls whose room is gone (`ListRooms`) or with no participant in the room for > 60 s
  (`ListParticipants`) → end + log.
- `POST api/livekit/webhook` (anonymous): verify the `Authorization` JWT (API secret; claim `sha256` =
  base64 SHA-256 of the body). Map identity → user. `participant_left` in an active private call marks that
  device Left but ends the call only if, after a 25 s grace check, no identity of that user is in the room.
  `room_finished` ends any unfinished call of that room. Dev URL: `http://host.docker.internal:8080/...`.
- `GET api/calls/{id}`, `GET api/calls/active?chatId=` (204 when none), `GET api/calls/history`.

## 7. Realtime event matrix

| Event | Sent to | When |
|---|---|---|
| MessageCreated | chat members | send, forward, system message, call log |
| MessageUpdated | chat members | edit, reaction, pin/unpin |
| MessagesDeleted | members (forEveryone) / caller (for me) | delete |
| MessagesRead | chat members | read up to seq |
| Typing | other members (not blockers) | hub SendTyping |
| ChatAdded | the user(s) | new group, added, joined, first private message, unhide |
| ChatUpdated | members | title/description/avatar/permissions/member count/active call/content protection |
| ChatRemoved | the user(s) | left, removed, group deleted |
| ChatMembersChanged | members | add/remove/role |
| ChatSettingsChanged | caller's user | pin/archive/mute/contact bar closed |
| PinnedChatsReordered | caller's user (all devices) | pinned chats rearranged (`PUT api/chats/pinned-order`) |
| HistoryCleared | caller / both | clear history |
| PinnedMessagesChanged | members | pin/unpin |
| PresenceChanged | related users (per-viewer privacy, not blocked) | online/offline |
| UserChanged | related users + the user's own devices | profile/privacy/phone/avatar/blocks |
| ContactsChanged | caller's user | contacts/blocks |
| MediaProcessed | uploader | processing finished |
| IncomingCall / CallUpdated | participants (group: members) | call lifecycle |
| SessionRevoked | that session (then its connections are aborted) | terminated |

## 8. Client

### 8.1 Client.Core (client-core)

Implement every interface in `Services/`, `Stores/`; register singletons in `AddSkyMessageClientCore`
(keep the signature). UI-free and WASM-safe (no blocking, no file system — use the platform interfaces).
The store rules at the top of `StoreContracts.cs` are mandatory (immutable snapshots, one mutation queue
per store, isolated event invocation, merge rules, buffered replay during resync, 5 Hz progress).

- **Endpoints**: candidates = `CustomServerUrl` ?? `DefaultServerUrl`, `ExtraServerUrls`, `FallbackServerUrls`
  (`UseHostOrigin` forces the page origin). Remember the server's `InstanceId`; fail over only to
  candidates with the same id (5 s probes); a custom server with a different id signs out first. React to
  `ISettingsService.Changed` (rebuild handlers, reconnect). `ResolveLiveKitUrl` and hub URLs resolve
  relative to `BaseUri` (strip the leading '/').
- **ISkyApi**: HttpClient from `IHttpHandlerFactory` (rebuilt on proxy changes); bearer via the token
  provider; 401 → refresh once and retry; network error → fail over once and retry idempotent calls;
  problem details → `ApiException(code)`; network → `ApiException(0, "network_error")`. Avoid the
  AuthService↔Api cycle (raw auth channel). `GetUsersAsync` splits > 200 ids.
- **IAuthService**: "auth.v1" in `ISecureStore`; refresh 60 s before expiry under `IInterProcessLock`
  ("sky-auth-refresh"), re-reading the store first; refresh 401 `refresh_token_invalid` → `LogoutAsync(false)`;
  network errors keep the session. `UserChanged` for me → `RefreshMeAsync`.
- **IRealtimeService**: hub URL `Resolve("hubs/realtime")`, `AccessTokenProvider`, transports WebSockets|SSE|
  LongPolling (WebSockets only when `AllowTransportFallback=false`), **`UseStatefulReconnect = true`**
  (server allows it), `HttpConnectionOptions.Proxy` from `IHttpHandlerFactory.CreateProxy` and
  `WebSocketFactory` from `CreateWebSocketConnector` when not null (add the Authorization header there),
  retry 0,1,2,5,10,15,30 s then every 30 s; after `Closed` restart with backoff + failover; `ReconnectNow`
  on network regain / app resume. `Resynced` only after reconnects that lost state; resync is lazy (chat
  list + open chat now, other chats on open). `SessionRevoked` → `LogoutAsync(false)`. Lifecycle → `SetActive`.
- **Failover hardening**: candidates include `ServerInfoDto.MirrorUrls` (stored per InstanceId); before
  sending tokens to a non-primary candidate verify `GET api/server/proof` with the media key; probes download
  ≥ 64 KB from `api/server/probe` within 10 s; stalls (no bytes for 15 s) on chunk uploads, media fetches or
  downloads abort the request and, when repeated, trigger failover.
- **Media URLs**: Direct mode = `BaseUri` + `ApiRoutes.Media.ContentPath(id, MediaSignature.Create(key,
  sessionId, id))` (cache signatures per id). AppProxy mode = `"/_sm/media/{id}/content"` /
  `"/_sm/media/{id}/thumbnail"`; `IMediaContentSource` serves them from `IMediaFileStore` or the server
  (bearer header, Range passthrough, writes thumbnails/small media through the file cache). Raise
  `Changed` on endpoint/auth changes.
- **IMediaUploader**: chunks of `ChunkSize`, ≤ 3 parallel, 5 retries with backoff, resume via `GetUpload`;
  progress 0–0.95 upload, then wait for `MediaProcessed` or poll every 1.5 s (≤ 10 min).
- **IMediaDownloadService**: per-media state, dedupe, cancel, LRU file cache honoring `CacheLimitMb`
  (`IMediaFileStore.TrimAsync`), auto-download policy from `DataSettings` × `INetworkStatusService.Kind`,
  open via `IFileOpener`, save via `IFileSaveService` (gallery for photos/videos when enabled). Browser:
  downloads go through a signed URL with `download=1`.
- **ISettingsService**: "settings.v1" JSON in `IPreferencesStore`, proxy password in `ISecureStore`
  ("proxy.password"), snapshots via `AppSettings.Clone()`; mobile default `SendByEnter=false`.
- **Stores**: instant start from `ILocalCacheStore` (keys prefixed `acct/{userId}/`: chat list, last 50
  messages per chat + their `SyncCursor`, users, outbox, drafts), then network refresh. Pending messages
  per the `MessageItem` doc; compression by settings (photo Low 1280/75, Standard 2560/82, High 4096/90;
  video 480p/720p/1080p ⇒ MaxDimension 854/1280/1920 and 1200/2500/5000 kbps; `CanCompressVideos=false` →
  upload the probed original); `Compress=false` → upload as `MediaKind.File`; typing actions while uploading.
  Outbox: persisted (attachments through `IOutboxFileStore` before becoming `Queued`), `Queued` while
  offline, resent with the same ClientMessageId; `Failed` only for non-transient errors. Resync on `Resynced`: chat list reload; for cached/open chats `afterSeq` pages +
  `/changes` from the stored cursor (apply tombstones, `HiddenMessageIds`, `ClearedUpToSeq`); verify the
  current call. `IUserStore` batches misses (50 ms, `GetUsersAsync`). `IDraftStore`, `IAccountLocalStore`
  per account.
- **ICallService**: per its doc. Before starting/accepting request Microphone (and Camera for video) via
  `IPermissionService`; call `ICallPlatformService.OnCallStartedAsync` only while in foreground. Incoming
  while in a call → `DeclineCallAsync(busy: true)`. Outgoing gets `JoinInfo` immediately. Ended calls stay 2 s with `EndStatus`. `ICallPlatformService` start/media/end;
  its `CommandRequested` and `ILocalNotificationService.CallActionInvoked` feed `RequestCommand`/accept/
  decline. Incoming in background → `ShowIncomingCallAsync`.
- **INotificationCoordinator** per its doc; badge = `TotalUnread` via `SetBadgeCountAsync`; notification taps
  open `DeepLinks.AppChatUri(chatId)`.
- **IClientBootstrapper** per its doc (no network wait; owns sign-in/out cleanup; enables
  `IBackgroundConnectionService` according to `KeepConnectedInBackground`).
- **ILinkBuilder**: `ServerInfoDto.PublicBaseUrl ?? BaseUri` + `DeepLinks.*Path`.
- Unit tests: `tests/SkyMessage.Client.Core.Tests` (project exists; add test files).

### 8.2 UI (ui-kit first, then ui-chat + ui-settings-calls in parallel)

**DI lifetimes**: `ILocalizer`, `FormatService`, `IMessagePreviewFormatter`, `EmojiCatalog`, theme/sound
*catalogs* are singletons with no JS/NavigationManager dependencies. `ThemeService`, `NavigationService`,
`DialogService`, `ToastService`, `SoundService` and all JS wrappers are scoped and unsubscribe from Core
events on Dispose.

**Design language** — our own: airy, rounded, calm. Light/dark, accent palette (violet default, blue,
teal, green, orange, pink, red, graphite + custom hex), built-in wallpapers we draw (soft multi-stop
gradients + optional subtle SVG pattern: "aurora", "dawn", "sea", "mint", "dusk", "plain"). Bubbles without
tails, radius from settings (default 16), consecutive messages from one sender grouped, time + status
inside the bubble; outgoing accent-tinted, incoming surface color; avatars = image or initials on a color
derived from the id. Respect `prefers-reduced-motion` and the Animations setting.

**Design tokens** (CSS custom properties on `:root`, theme via `html[data-theme=light|dark]`, applied by
`ThemeService` and early by `js/boot.js`): `--sm-accent --sm-accent-hover --sm-accent-soft --sm-on-accent
--sm-bg --sm-surface --sm-surface-2 --sm-surface-3 --sm-text --sm-text-2 --sm-text-3 --sm-link --sm-border
--sm-divider --sm-danger --sm-success --sm-warning --sm-online --sm-bubble-out --sm-bubble-out-text
--sm-bubble-out-meta --sm-bubble-in --sm-bubble-in-text --sm-bubble-in-meta --sm-chat-bg --sm-chat-wallpaper
--sm-shadow-1 --sm-shadow-2 --sm-radius-s --sm-radius-m --sm-radius-l --sm-bubble-radius --sm-font
--sm-font-size --sm-header-h --sm-tabbar-h --sm-sidebar-w --sm-safe-top --sm-safe-bottom --sm-safe-left
--sm-safe-right --sm-kb-inset --sm-ease --sm-dur`. The MAUI host sets `--sm-safe-*` and `--sm-kb-inset`
from native insets (CSS uses `var(--sm-safe-top, env(safe-area-inset-top))`).

**Layout**: phones (< 768 px): one pane, slide transitions, bottom tab bar (Chats · Contacts · Calls ·
Settings) on top-level screens, full-screen chat with back. ≥ 768 px: resizable left column (300–480 px)
with an icon rail + list, routed content on the right, third info column ≥ 1280 px (else overlay).

**Navigation & Back**: every overlay (dialog, sheet, context menu, media viewer, selection mode, call
screen expanded) pushes a history entry and closes on `popstate`, so Android Back closes overlays first.
`NavigationService` is the only consumer of `IDeepLinkSource` and `DeepLinks.TryParse`; signed-out users
opening `/u/*` or `/join/*` go to `/welcome` and the pending link is opened after sign-in (no anonymous
previews). `ThemeService` calls `ISystemUiService.SetSystemBarsStyleAsync` on theme changes; tap feedback
uses `IHapticsService` when `InAppVibrate`.

**Routes**: `/`, `/welcome`, `/login`, `/register`, `/chat/{ChatId:guid}`, `/chat/{ChatId:guid}/info`,
`/chat/{ChatId:guid}/media`, `/user/{UserId:guid}`, `/u/{Username}`, `/join/{Token}`, `/contacts`,
`/contacts/import`, `/new-group`, `/search`, `/calls`, `/settings`, `/settings/{Section}`.

**People & data rules**: render people via `IUserStore.Get(id) ?? embedded` / `DisplayNameOf`; chat titles
via `IChatListStore.TitleOf/AvatarOf`; media URLs only via `IMediaUrlBuilder` (re-render on its `Changed`);
use store methods over `ISkyApi`; direct `IRealtimeService` subscribers refetch on `Resynced`; call
`IAuthService.UpdateMe` after profile/avatar/phone/privacy changes. **Links**: render only allow-listed
schemes (http, https, mailto, tel), `rel="noopener noreferrer"`, open via `IExternalLauncher`, ask for
confirmation when the visible text's host differs from the target; our own `DeepLinks` navigate in-app.
Never use `MarkupString` for user content.

**ui-kit** builds: `Routes.razor` (router, auth gate, calls `IClientBootstrapper.StartAsync` once, error
boundary), `UiSetup.AddSkyMessageUI()`, the shell (`Shell/`: layouts, tab bar, icon rail, panes, transitions,
connection status bar "Waiting for network / Connecting… / Updating…" from `INetworkStatusService` +
`IRealtimeService`, in-app notification banner + tone), kit components (`Kit/`: Icon, Avatar (+online dot),
Button, IconButton, TextField, TextArea, Switch, Checkbox, Radio, Slider, Select, ListItem, Section, Badge,
Spinner, Skeleton, EmptyState, SearchBox, Dialog, ConfirmDialog, BottomSheet, ContextMenu (long-press +
right-click), Toast, Tabs, SegmentedControl, ProgressRing, QrCode (QRCoder PNG data URL), ColorSwatch,
**EmojiPicker** + `Kit/Emoji/EmojiCatalog.cs` (curated Unicode by category, recent emoji via
`IAccountLocalStore`)), services (`Services/`: `ILocalizer` (+ Russian plural rules), `ThemeService` (+
`Accents` and `Wallpapers` catalogs), `NavigationService`, `DialogService`, `ToastService`, `ISoundService`
(`Tones` catalog incl. message tones "chime"/"pop"/"glass", ringtones "classic"/"soft"/"bright", ringback,
busy, call-end; `PlayMessageTone(key)`, `StartRingtone(key)`, `StartRingback()`, `Stop()`), `FormatService`
(times, durations, sizes, last-seen phrases), `IMessagePreviewFormatter`, JS wrapper for `app.js`), and
features: Auth (welcome with server picker + connection test, login, register with live username check
and invite code when required), ChatList (avatar, title, preview incl. "Draft:", sender for groups, time,
unread + mention badges, pinned/muted marks, typing preview, actions pin/mute/archive/delete, archived
folder), Contacts (online first, add by username, phone-book import with confirmation list showing
@username + avatar, share my profile link), Profile (avatar, name, @username, bio, phone, message/call/
video, share link + QR, add/rename/remove contact, block), Groups (new group → `CreateGroupResultDto`
failures offered as invite link; group info with members, roles, add, invite link copy/share/revoke, edit,
leave/delete), Search (users, chats, messages; recent searches via `IAccountLocalStore`).
**Stubs** ui-kit creates for later agents (keep these names/parameters): `Features/Chat/ChatPage.razor`
(`@page "/chat/{ChatId:guid}"`, `[Parameter] Guid ChatId`), `Features/Calls/CallOverlay.razor` (no
parameters, rendered once in the shell above everything), `Features/Calls/GroupCallBanner.razor`
(`[Parameter] Guid ChatId`), `Features/Settings/SettingsPage.razor` (`@page "/settings"` and
`@page "/settings/{Section}"`), `Features/Calls/CallsPage.razor` (`@page "/calls"`).
ui-kit also writes `docs/UI-KIT.md`: how to use every kit component and service (with snippets) — the next
two agents rely on it.

**ui-chat** builds the chat screen (`ChatPage`): sets/clears `ActiveChatId`; header (avatar, title,
presence/typing/members, call buttons, search, menu), `GroupCallBanner` slot, pinned bar
(`IMessageStore.GetPinned`), message list (load older on scroll top with stable position, newer after a
jump, date chips, "Unread messages" divider, scroll-to-bottom button with unread count and mention jump,
`JumpRequested` highlight, read tracking: `MarkReadAsync(highest visible confirmed seq)` only while in
foreground), bubbles for every type (text with entities and safe auto-links, reply preview → jump,
forwarded header, album grid as justified rows of 1–3 tiles, single photo/video with `MiniThumbnail` blur
placeholder and duration, file card with download progress via `IMediaDownloadService`, voice player
(waveform SVG, play/pause, scrub, speed 1×/1.5×/2×, played state via `IAccountLocalStore`), round video
note (muted autoplay when visible, tap = sound + progress ring), call log, system messages, reactions bar,
edited mark), pending states (Preparing/Uploading progress, Queued clock, Failed retry/delete), composer
(auto-grow, `EmojiPicker`, attach menu: gallery, file, camera photo/video, "send without compression";
paste and drag-and-drop on desktop; reply/edit bars; Enter per settings; drafts via `IDraftStore`; member
autocomplete inserting `Mention{UserId}` entities), **voice recording** (hold to record, slide to cancel,
slide up to lock, live waveform + timer, tap toggles mic ↔ camera mode), **video note recording** (round live
preview, ≤ 60 s, progress ring, flip camera, lock, cancel), context menu (reactions + Reply, Copy, Edit,
Pin, Forward (chat picker), Select (multi: forward/delete), Save, Delete for me/everyone), media viewer
(swipe, zoom/pan, save, forward), in-chat search, shared media page (`/chat/{id}/media`). Recording uses
`getUserMedia` + `MediaRecorder` in `recorder.js` with device ids from `DeviceSettings`, returns the blob via
`IJSStreamReference`; the server normalizes formats.

**ui-settings-calls** builds Settings (`/settings`, `/settings/{section}`): profile edit (avatar upload,
name, username with availability, bio), phone (detect via `IPhoneNumberService` on Android, manual entry with
region, unlink, "who can find me by number"), privacy & security (all levels, blocked users, active
sessions, change password, delete account, sign out), notifications & sounds (toggles incl. keep connected
in background + battery exemption via `IBackgroundConnectionService`; on iOS a hint that the app must stay
open to receive calls until push lands; tone pickers with preview through `ISoundService`), appearance (theme, accents, wallpapers with live preview, font size, bubble radius,
animations, compact list), chat settings (send by Enter, quick reaction via `EmojiPicker`, autoplay, voice
speed), **devices** (mic select + live level meter + record/playback test; camera select + preview +
mirror; speaker select + test tone (`setSinkId` where supported); echo cancellation / noise suppression /
auto gain; call video quality; phones: default audio route via `IAudioRouteService`), data & storage
(auto-download Wi-Fi/mobile, photo/video quality, save to gallery, cache size + clear via
`IMediaDownloadService`, low data mode), language, connection (server + status/transport, test, custom
server, fallback addresses, proxy HTTP/SOCKS5 with test via `IServerEndpointService.TestAsync`, hidden when
`IHttpHandlerFactory.SupportsProxy` is false; text: "The proxy applies to messages and media; calls may not
work through a proxy"), about (version, server version, licenses of everything
shipped). Calls: `CallOverlay` (incoming screen with accept audio/video / decline + ringtone; outgoing with
ringback; 1:1 layout (remote full, draggable local PIP) and group grid with active speaker; controls mute,
camera, flip, audio output, screen share on desktop, hang up; minimize to a top bar; timer; quality;
reconnecting; ended screen; when the local camera track ends/mutes after the app returns from background,
offer to re-enable it) executing `ICallService.CommandRequested` and reporting `Report*`;
`GroupCallBanner`; `/calls` history (icons, call back, missed filter). `calls.js` wraps **livekit-client**
(vendor the UMD build of npm `livekit-client@2.22.4` + LICENSE into `wwwroot/lib/livekit/`), maps
participants by `metadata.userId` (identity is per device), reports through a `DotNetObjectReference`.
`devices.js` serves both settings and calls.

**Localization**: `ILocalizer` reads embedded JSON `Localization/{lang}/{area}.json` (flat keys like
`"chat.composer.placeholder"`); areas owned as in §4; error codes → `errors.json` (`"error.<code>"`).

### 8.3 App (app-platform)

- `MauiProgram`: BlazorWebView; `AddSkyMessageClientCore(o => { o.DefaultServerUrl = …; o.MediaUrlMode =
  MediaUrlMode.AppProxy; })` (DEBUG: Android emulator `http://10.0.2.2:8080/`, iOS simulator/Mac
  `http://localhost:8080/`); `AddSkyMessageUI()`; every platform interface (singletons).
  `wwwroot/index.html` includes `_content/SkyMessage.Client.UI/css/app.css`, `SkyMessage.App.styles.css`,
  `_content/SkyMessage.Client.UI/js/boot.js`, `_framework/blazor.webview.js`.
- Media proxy: `BlazorWebView.WebResourceRequested` is synchronous — for paths where
  `IMediaContentSource.CanHandle` is true, call `GetHead` and `SetResponse(status, reason, headers,
  OpenBodyAsync(...))` without awaiting (206 reason "Partial Content"). If `<video>` seeking is unreliable on
  a platform, set `ClientCoreOptions.AppProxyKinds` to exclude Video/VideoNote there and report it.
- WebView (Android): wrap the default chrome client to grant `getUserMedia` after runtime permissions
  (PermissionManagingBlazorWebChromeClient pattern) with file chooser passthrough; wrap the WebViewClient
  (forward ShouldInterceptRequest/ShouldOverrideUrlLoading/page events/OnReceivedHttpAuthRequest) and
  override `OnRenderProcessGone` → return true and rebuild MainPage's BlazorWebView (Core singletons survive;
  calls re-join via `ICallService.JoinAsync`); `MediaPlaybackRequiresUserGesture=false`; debug-only mixed
  content + WebView debugging; MainActivity `LaunchMode.SingleTask`, full `ConfigurationChanges` (incl.
  Keyboard, KeyboardHidden, Navigation, Locale, LayoutDirection, FontScale, ColorMode), `OnNewIntent` +
  launch intent → `IDeepLinkSource`; Back via `MainPage.OnBackButtonPressed`/`OnBackPressedDispatcher`
  (WebView back when possible, else `MoveTaskToBack(true)`; never finish); MainPage `SafeAreaEdges=None`,
  soft input AdjustResize, system-bar/cutout/IME insets pushed into `--sm-safe-*` and `--sm-kb-inset`.
  iOS/Mac: wrap MAUI's existing `WKUIDelegate` (forward the JS panel methods) and add
  `RequestMediaCapturePermission` → Grant; only `--sm-safe-*` (keyboard inset stays 0, WKWebView resizes).
  Never enable `AppHostAddressAlways0000` (`app://0.0.0.0` is refused by WebKit).
- Android manifest: INTERNET, ACCESS_NETWORK_STATE, RECORD_AUDIO, CAMERA, MODIFY_AUDIO_SETTINGS,
  READ_CONTACTS, READ_PHONE_NUMBERS (fallback detection only; SIM/network country needs no permission),
  POST_NOTIFICATIONS, FOREGROUND_SERVICE + _MICROPHONE + _CAMERA + _REMOTE_MESSAGING (background connection —
  not dataSync, which is capped at 6 h/day on targetSdk 35+), RECEIVE_BOOT_COMPLETED (restart the background
  service on boot / package replaced when enabled), WAKE_LOCK, VIBRATE, BLUETOOTH_CONNECT,
  REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, USE_FULL_SCREEN_INTENT, WRITE_EXTERNAL_STORAGE maxSdkVersion 28;
  `allowBackup="false"`; `<queries>` for IMAGE_CAPTURE, VIDEO_CAPTURE, VIEW (https/mailto/tel) and SEND;
  DEBUG-only `UsesCleartextTraffic`; `skymessage://` and notification deep links. Call service type derived
  from granted permissions (microphone; camera added only in foreground); Accept/AcceptVideo notification
  actions are Activity PendingIntents (bring MainActivity forward, then start the service); Decline/Hang up
  may be broadcasts. Notification sounds: render our synthesized tones to small WAV files (script) in
  Resources/Raw; one channel per tone key (recreate on change); incoming calls ring with FLAG_INSISTENT or a
  looping ringtone stopped on accept/decline/foreground.
- Apple: Mac `Entitlements.plist` camera, audio-input, addressbook, user-selected + downloads read-write,
  `keychain-access-groups` = `$(AppIdentifierPrefix)com.skymessage.messenger`; iOS Entitlements with the
  keychain group (simulator); Info.plist NSCamera/NSMicrophone/NSContacts/NSPhotoLibraryAdd/NSLocalNetwork
  usage strings, UIBackgroundModes audio; set `UNUserNotificationCenter.Current.Delegate` in
  `FinishedLaunching` before base; `ISecureStore` catches Keychain errors (degrades in DEBUG). Caches and
  the outbox under Library/Caches (excluded from backup).
- Platform services (Android complete; iOS/Mac compiling with best-effort implementations;
  `IsSupported=false` where impossible): PlatformInfo (+ CountryCode from SIM/network/locale), SecureStore,
  PreferencesStore, LocalCacheStore (files), OutboxFileStore, FilePicker (mixed photo+video selection
  natively: Android `PickMultipleVisualMedia(10)` ImageAndVideo via ActivityResultRegistry + persistable URI
  grants; iOS/Mac `PHPickerViewController` images+videos, copy files inside the completion handler; cap 10 in
  code; FilePicker for files; capture), MediaCompressor (**platform codecs, no SkiaSharp in the app**: Android
  `ImageDecoder` with target-size subsampling (applies EXIF, decodes HEIF; API 26–27 `BitmapFactory`
  inSampleSize + AndroidX ExifInterface) → `Bitmap.Compress(JPEG)`; iOS/Mac ImageIO
  `CGImageSourceCreateThumbnailAtIndex` (with transform) → JPEG via `CGImageDestination` without metadata;
  fallback = Probe + original (the server converts HEIC). Android video via Media3 Transformer on the main
  Looper: tone-map HDR to SDR (OpenGL), `Presentation` scaling, `DefaultEncoderFactory` bitrate, progress
  polling ~4 Hz, cancel; on `ExportException` fall back to the original. iOS/Mac AVAssetExportSession preset
  by MaxDimension (bitrate best effort). Probe via MediaMetadataRetriever / AVAsset), PhoneNumberService
  (Phone Number Hint API first via ActivityResultRegistry → E.164 candidates; SubscriptionManager fallback
  without Google services), DeviceContactsService (with NORMALIZED_NUMBER → E164Numbers), PermissionService
  (incl. Bluetooth, FullScreenIntent, Limited), LocalNotificationService (channels messages/calls,
  full-screen incoming-call notification with Accept/Decline actions, badge), AudioRouteService,
  CallPlatformService (foreground service microphone|camera, wake lock, ongoing-call notification with
  Mute/Hang up → `CommandRequested`), BackgroundConnectionService (low-priority persistent foreground service
  + battery exemption prompt), NetworkStatusService (Connectivity), SystemUiService, HapticsService,
  ShareService, FileSaveService (+ gallery), FileOpener, MediaFileStore (cache dir), AppLifecycle,
  ExternalLauncher, DeepLinkSource (intents/URL opens/notification taps), InterProcessLock (in-process),
  AudioRouteService (route ids: `AudioDeviceInfo.Id` on API 31+, speakerphone/SCO below; AVAudioSession on
  iOS; re-apply the preferred route after publish and on device changes), HttpHandlerFactory
  (`SocketsHttpHandler` with Happy Eyeballs `ConnectCallback` (IPv6 first, IPv4 after 250 ms), HTTP/SOCKS5
  proxy, per-attempt connect timeouts, pooled lifetime 5 min; iOS: `NSUrlSessionHandler` when no proxy (or
  iOS 17+ proxy configurations); `CreateProxy` and `CreateWebSocketConnector` for SignalR; Android WebView
  `ProxyController` (androidx.webkit) for HTTP proxies, answer 407 in the wrapped WebViewClient; SOCKS with
  credentials cannot apply to the WebView — warn; WebRTC media never uses a proxy).
- Native strings via `ILocalizer` with `Localization/*/platform.json`.
- Replace the template icon/splash/titles with our own (gradient cloud, no tail).
- Web host (`SkyMessage.Web`): `UseHostOrigin`, optional `ServerUrl` in `wwwroot/appsettings.json` for
  standalone dev, platform implementations through `wwwroot/web-platform.js` (IndexedDB cache + outbox
  blobs, localStorage for preferences/secure store, hidden file input, canvas image compression,
  notifications through a minimal no-cache `sw.js` + `registration.showNotification` (Chrome Android has no
  `new Notification()`), visibilitychange, navigator.onLine/connection, navigator.locks with a localStorage
  lease fallback, navigator.share/clipboard, anchor download, deep link = initial path), `SupportsProxy =
  false`, PWA manifest + icons, index.html with the same includes (`SkyMessage.Web.styles.css`,
  `_framework/blazor.webassembly.js`). Device testing of the web client needs https or localhost (secure context).

## 9. Local environment

- `docker compose up -d db livekit` (Postgres 17 on 127.0.0.1:5432; LiveKit signaling 127.0.0.1:7880,
  media 7881/tcp + 7882/udp on the LAN IP from `LIVEKIT_NODE_IP` in `.env`; webhooks →
  `host.docker.internal:8080`). The compose `api` service runs in Development with dev secrets.
- API: `dotnet run --project src/Server/SkyMessage.Api` → http://0.0.0.0:8080 (Development settings
  contain dev keys; the API refuses dev secrets outside Development). Web client at http://localhost:8080/.
- Android emulator reaches the host at `10.0.2.2`.

## 10. Not in v1 (roadmap)

Push delivery (FCM/APNs, encrypted payloads — iOS first: until then iPhones only get messages/calls while the
app is open; the UI says so), CallKit/ConnectionService system call UI, relays in Russia (a plain reverse
proxy works for REST/SignalR/media/signaling, but calls also need TURN over TLS on the relay), E2E secret chats,
channels, bots, stickers/GIFs, polls, scheduled messages, chat folders, link previews, 2FA, multi-account,
translation, certificate pinning, production deployment (Caddy TLS, TURN over TLS on 443 via SNI routing,
backups, separate prod compose with mandatory secrets), app links (`assetlinks.json`, AASA).
