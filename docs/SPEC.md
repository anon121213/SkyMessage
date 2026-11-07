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
   Our brand: **SkyMessage**, default accent violet `#6A4BFF`, logo «Горизонт» = an outgoing message bubble
   (no tail: one tight square corner) cut by a horizon, flat, on a Night-indigo ground (`docs/brand`), our
   own gradient/pattern wallpapers, sounds synthesized from
   first principles (`tools/tones`, no samples). Icons: Lucide (ISC) from the `lucide-static` npm package, license shipped in `wwwroot/licenses/`.
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
- JSON: `SkyJson` everywhere (camelCase, enums as strings). `SystemEventType` grows with releases: apps read an
  unknown name as `Unknown` (`TolerantEnumConverter`) and show a generic service message.
- Routes: actions use `ApiRoutes.*` template constants on the action attribute (no controller-level
  `[Route]`): `[HttpGet(ApiRoutes.Chats.ById)]`. Clients use the `*Path(...)` builders.
- Auth: the fallback policy requires an authenticated user. **Anonymous endpoints (exhaustive):**
  register, login, refresh, username-available, server/info, health, media content/thumbnail (signature
  or bearer), LiveKit webhook (own signature), app/version and the APK downloads under `/updates`. JWT `sub` = user id `ToString()`, `sid` = session id; the
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
- **Migrations** (docs/DEV-WORKFLOW.md, "Server changes"): one EF migration per feature, named after it and made with
  `dotnet ef migrations add`; never edit an existing migration — if `develop` gained another one meanwhile, the
  integrator re-creates the feature's migration on top. The API applies them on startup (`Server:MigrateOnStartup`)
  and the integration tests' `ApiFactory` applies them too; `EnsureCreated`
  (`PersistenceSetup.EnsureCreatedWithExtrasAsync`) is only the fallback for a model without migrations (fixtures).
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
(ownership transfer rules), keep messages (shown as from a deleted account). Saved Messages, contacts, own blocks, chat
folders and chat list settings are deleted explicitly (the tombstone row stays, so no cascade fires).

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

**Avatar history** (`AvatarPhoto`, shared with groups, `AvatarHistory` in Application/Common): every photo that
becomes the avatar is kept (newest `SetAt` first, at most `Limits.AvatarHistoryMax` = 30, the oldest go); setting a
photo of the history again moves it to the front. `DELETE api/users/me/avatar` leaves no photo and drops that photo
from the history (older ones stay, seen only by the owner until a new photo shows them again — the apps say so and
offer to delete them too); `DELETE api/users/me/avatars` leaves no photo and no history; `DELETE
api/users/me/avatars/{mediaId}` drops one photo — the current one is replaced by the newest remaining (none left → no
photo); unknown → 404 `media_not_found`. `MeDto.EarlierAvatarCount` counts the history without the current photo
(every `MeDto`, auth responses included), so Settings opens the earlier photos even without a current one.
`GET api/users/{id}/avatars` → `AvatarPhotoDto[]` (current first): for others only while the person has a current
photo and `CanSeeProfilePhoto` allows it, else `[]`; unknown/deleted users → 404. The migration put every current
avatar into its history.

**Profile extras**: `PATCH api/users/me` also takes `ProfileColor` (a `ProfileStyle.Colors` key, "" = none) and
`Links` (≤ `Limits.ProfileLinksMax` = 3, the whole list; URLs normalized by `ProfileStyle.NormalizeLinkUrl`: absolute
http/https with a host, "https://" added, ≤ 512 chars; titles ≤ 32; duplicates dropped; anything else 400
`validation_failed`). `PUT api/users/me/cover {Gradient | MediaId}` (a `ProfileStyle.CoverGradients` key or the
caller's own `Ready` photo, exactly one) / `DELETE`. `PUT api/users/me/birthday {Day, Month, Year?}` (`Birthdays.IsValid`:
a real date, Feb 29 needs a leap year when the year is given, year ≥ 1900, not in the future) / `DELETE`.
What others see: `UserSummaryDto.ProfileColor` and `.Birthday` (per the **Birthday** privacy level, default
Contacts; the year only if given), `UserProfileDto.Cover` (a photo cover follows the ProfilePhoto level; then the
gradient is gone) and `.Links`. Someone who blocked the viewer (or a deleted account) shows none of it. Old apps
save privacy without `Birthday`: a missing `PrivacySettingsDto.Birthday` keeps the stored level. Account deletion
clears all of it and the avatar history.
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
Phone-book sync: `POST api/contacts/bulk {Synced: true}` marks new contacts `ContactSource.PhoneSync`
(`ContactDto.IsSynced`) and gives synced ones the name sent (the phone book's); the user's own contacts stay as they
are. Adding, importing (bulk without `Synced`) or renaming a synced contact makes it Manual. `DELETE
api/contacts/synced` → `{Removed}` removes only the synced ones. `POST api/users/find-by-phones` allows
`Limits.PhoneLookupsPerWindow` requests per user per `Limits.PhoneLookupWindowMinutes` (fixed window, 429
`rate_limited`), shared by the import and sync.

**Privacy** (`IPrivacyService`): blocks first — if the owner blocked the viewer every level is Nobody.
`CanMessage(from,to)`: not blocked AND (`to.Messages == Everybody` OR `to` has `from` in contacts OR `to`
already sent a message in their private chat); Nobody ≈ Contacts. `CanCall` per `to.Calls`.
`CanAddToGroup` per `GroupInvites` (Nobody ≈ Contacts). `CanSeeProfilePhoto` per `ProfilePhoto` (also the avatar
history and a photo cover). Birthday per `Birthday` (Nobody means nobody).

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

**App updates** `GET api/app/version` (anonymous, `no-cache`) → `AppVersionDto{Android?, Web?, ServerVersion}`:
the newest Android build from the manifest CI writes into `Updates:Path` (`latest.json`: versionCode, versionName,
file, sha256, size, notes, minVersionCode; offered only when the named APK exists with exactly that size) and the
web client version (= the server build). APKs are static files under `/updates/*.apk` (Range support, nothing else
in the directory is served); `GET|HEAD updates/latest.apk` redirects to the current one. Clients: `IAppUpdateService`
(Android downloads, verifies and installs through `IAppUpdateInstaller`; the browser reloads into a new web client).
Releases and CI: `deploy/vps/README.md`.

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
- `PATCH api/chats/{id}`, avatar `PUT/DELETE` (caller's own Photo, or one of the group's avatar history — any admin
  may set it again), system messages, `ChatUpdated`. The group's avatar history works like a profile's:
  `GET api/chats/{id}/avatars` (members; while the group has no photo only who may change the info, others get
  `[]`), `DELETE api/chats/{id}/avatars/{mediaId}` (who may change the info; the current one → the newest remaining
  takes over with `PhotoChanged`, or `PhotoRemoved`), `ChatUpdated` either way; `DELETE api/chats/{id}/avatars` (who
  may change the info) leaves no photo and no history (`PhotoRemoved` when there was a photo); removing the avatar
  drops it from the history; a group created with a photo starts its history. `ChatDetailsDto.EarlierAvatarCount`:
  the earlier photos this viewer may open.
- Members: add (AddMembers permission or admin; **banned users: only admins can add, which lifts the ban**),
  remove (admin; owner removes admins; creates a `ChatBan`; then calls every `IChatMembershipObserver`),
  role (owner; Owner = transfer), leave (ownership passes to the oldest admin, else oldest member; last
  member out deletes the group). `ChatAdded` / `ChatRemoved` / `ChatMembersChanged` + `ChatUpdated`.
- Invite links (admins, or members when AddMembers is allowed; token = 16 random bytes base64url):
  `POST/DELETE api/chats/{id}/invite-link`, `GET/POST api/chats/join/{token}` (authenticated; banned users
  and invalid tokens → 404 `invite_invalid`).
- `PUT .../settings` → `ChatSettingsChanged` to the caller. `POST .../read {upToSeq}`: clamp, only up,
  `MessagesRead` to members. `POST .../clear-history`, `DELETE api/chats/{id}?forEveryone=`.
- `PUT api/chats/pinned-order {chatIds}`: my own order of pinned chats (`PinOrder`, higher first; a new pin goes on
  top) → `PinnedChatsReordered` to my devices.
- `PUT api/chats/{id}/content-protection {enabled}`: groups — admins; private chats — each side turns their own part
  on or off (`ProtectContentBy`, protected while anyone's part is on); Saved → 400. Blocked either way → switching
  on is 403 `user_blocked`, taking one's own part back still works but posts no notice. Deleting an account removes
  its part (no notice). A real change → `ChatUpdated` + system `ContentProtectionEnabled/Disabled`. Forwarding out
  of a protected chat → 403 `forward_restricted`.

Folders and the archive (`IChatFolderService`, `ApiRoutes.Folders`):
- My folders (≤ `Limits.MaxChatFolders`, name 1..24, icon = one emoji or an app icon key) and my chat list settings
  are one state (`ChatFoldersDto`) with a revision: every real change bumps it and sends the whole state to all my
  devices as `ChatFoldersChanged` (a change that changes nothing is silent); every call answers with that state. Changes
  run under a row lock of my `ChatListPreferences` (created by the first change with `INSERT … ON CONFLICT DO NOTHING`),
  so devices apply one after another (parallel creates stop exactly at the limit). `GET api/folders`;
  `PUT api/folders/{id}` (`SaveChatFolderRequest`): with `Create` it creates the folder with that id (the app picks it: a
  repeat is harmless), else it edits it — a folder that no longer exists (deleted on another device) or someone else's id
  → 404 `folder_not_found`, never a new folder; a 21st folder → 400 `folder_limit`. Its fields are nullable: null keeps
  what is stored (an app that does not know a newer field never resets it; fields added later are nullable too), and
  the folder is checked whole once the edit is laid over it (it must take in something; no chat both included and
  excluded). Names: line breaks become spaces, trimmed, something visible (`ChatFolderRules.NormalizeName/IsValidName`: no
  control characters, not only spaces or zero-width characters). Icons: an app icon key or exactly one emoji
  (`ChatFolderRules.IsOneEmoji`: one grapheme cluster with a non-ASCII code point; ASCII only as a keycap's 0–9 # *; no
  bidi or other invisible format characters besides ZWJ and the tags of subdivision flags). Ids that are not (or no
  longer) my chats are dropped from the folder's included, excluded and pinned lists on every write of that folder;
  `DELETE`; `PUT api/folders/order` (the listed folders take the places they held, like pinned chats);
  `PUT api/folders/settings` (`UpdateChatListSettingsRequest`: only the settings it sets change, so two devices switching
  different settings both keep theirs).
- Which chats a folder shows is `Contracts.ChatFolderRules.Contains` (`ChatFolderMatcher` for many chats: the same rules
  with hash sets), run by the server and every app on the viewer's `ChatSummaryDto`: not excluded by id, included by id or by type (private chats by whether the other person is in my
  contacts; groups; channels once they exist; Saved Messages only by id), and not filtered out by the exclusions (muted,
  read = no unread messages or mentions, archived), which apply to the chats included by id too. Booleans, not a flags
  enum, so older apps read folders of a newer server.
- Each folder has its own pins (`PinnedChatIds`, top first): `PUT/DELETE api/folders/{id}/pins/{chatId}` (a new pin goes on
  top; only my visible chats, else 404 `chat_not_found`), `PUT api/folders/{id}/pinned-order` (`PinOrderRules.Arrange`).
  They only order the folder; the global pins (`PinOrder`) belong to "All chats".
- `POST api/folders/{id}/read` marks every unread chat of the folder read (`POST api/folders/all/read`: every chat outside
  the archive), only forward, with the usual `MessagesRead` per chat.
- Where chats land (`ChatPlacement`, by the member's settings; no row = the defaults): with `ArchiveNewFromStrangers`
  a chat from a stranger starts in the archive, muted (`MutedUntil` = forever: spam neither shows nor rings) — a private
  chat that appears (or reappears after "delete for me") with a message, a forward, a call log or a notice from someone
  not in my contacts, a group that someone not in my contacts creates with me or adds me to (joining by a link is my own
  doing). `KeepArchived` (default on, the old behavior) keeps archived chats archived; off, a new message, forward or
  call log from someone else brings an archived chat back (`ChatSettingsChanged` to that member) unless it is muted for
  them (a mute that ran out no longer counts) or it came from a stranger (still not a contact; for a group: whoever
  added me) while they send those to the archive. Notices never bring a chat back. `HideArchive` only tells the apps to
  drop the archive row.

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
  A sticker (`StickerId`, of a pack that still exists, else 404 `sticker_not_found`) or one of my saved GIFs again
  (`GifMediaId`: an animation I can still see, `IMediaAccessService`, else 404 `media_not_found`) goes alone (no text,
  no `MediaIds`, a reply is fine; else 400 `validation_failed`), nothing is uploaded: the existing media is attached
  (Photo / Video message; groups need the SendMedia permission). A sent sticker then goes first among my recent
  stickers (§6.6).
- Forward `POST .../messages/forward` (target chat in route; `ClientMessageIds` parallel to `MessageIds` for
  idempotency): copy text/entities/attachments with `ForwardInfo` (keep the original forward info); skip
  system/call messages.
- Edit (own, ≤ `EditWindowHours`, not system/call, not a sticker: 403 `message_not_editable`) → `EditedAt`, mentions
  re-indexed → `MessageUpdated`.
- Delete `POST .../messages/delete`: forEveryone — own always; private chats any message; groups others'
  only for admins → tombstone (clear text/entities/attachments/reactions/mentions/pin), fix
  `Chat.LastMessageId` under `ChatSequence.LockChatAsync` → `MessagesDeleted{ForEveryone=true}`. For me →
  `HiddenMessages` → `MessagesDeleted{ForEveryone=false}` to the caller.
- Reactions `PUT/DELETE .../reaction` (one per user, ≤ 16 chars) → `MessageUpdated`. Pins `PUT/DELETE .../pin`
  (groups: admin or PinMessages) → system `MessagePinned`, `PinnedMessagesChanged`, `MessageUpdated`.
- Search: `GET .../messages/search?q=` → `MessagesPageDto` (ILIKE with escaped `%_`, newest first),
  `GET api/search/messages?q=`, `GET api/search?q=` (`GlobalSearchResultDto`; users like users/search).
  Shared media `GET api/chats/{id}/media?kind=` (stickers are not part of a chat's media).
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
    (clients strip metadata); larger or other formats → JPEG q85 (metadata stripped). A GIF with one frame as is; an
    animated one becomes a muted looping MP4 (`AnimationProcessor`: ffmpeg through the `gif` demuxer only, file
    protocol only; transparent parts over white, H.264 yuv420p, even sides, long side ≤ `Limits.AnimationMaxSide`
    = 720, no audio, `+faststart`, ≤ 120 s) with its first frame as the poster: the media turns into `Video` with
    `IsAnimation` (`MediaDto.IsAnimation`); a failed conversion or no ffmpeg keeps the GIF as it was. HEIC →
    ffmpeg → JPEG, else Failed. Thumbnail ≤ 480 JPEG q75 (`MediaThumbnail`), mini ≈ 24 px (`MiniThumbnail`).
  - Video: ffprobe; H.264/AAC MP4 within `VideoMaxDimension` → remux `+faststart`; else transcode (CRF
    `VideoCrf`, yuv420p, AAC 128k); poster thumbnail.
  - VideoNote: always transcode to a centered square `VideoNoteSize`², ≤ 60 s, H.264 + AAC 64k mono.
  - Voice: AAC-LC 48 kbps mono 48 kHz `.m4a` (`audio/mp4`), duration, waveform (100 peak buckets 0..255).
  - Audio: duration. File: nothing (original bytes are sacred: "send without compression" uploads Files).
  Replace chunks, update metadata, `Ready`, then `MediaProcessed` to the owner (also on `Failed`). Missing
  ffmpeg → store as-is and log.
- `GET api/media/{id}/content?t=&download=1`, `.../thumbnail?t=` (anonymous endpoints): require a bearer
  user or a valid `?t=` signature for that media id. Access: owner; or the image of a sticker of an existing pack (anyone
  signed in, §6.6); or the current avatar, a photo of the avatar
  history or the cover photo of a user whose photo the viewer may see (`CanSeeProfilePhoto`); or the current avatar
  of a chat the viewer is a member of, or a photo of its history while it has a current one (always for who may change
  its info); or attached to a non-deleted message in a chat where the viewer is a member and the message is visible
  to them. Positive checks cached 60 s. Seekable chunk-backed stream with `EnableRangeProcessing`,
  `ETag`, `Cache-Control: private, max-age=31536000, immutable`, `nosniff`, `CSP: sandbox`; Files and
  dangerous types (html, svg, xml, js) → `attachment` (+ octet-stream when dangerous).
- `GET api/media/{id}` → `MediaDto` (same access rule).
- Cleanup hourly: `Uploading` older than `IncompleteUploadTtlHours`; unreferenced `Ready` media older than 7 days
  (avatar histories, covers, stickers of packs and saved GIFs count as references).

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

### 6.6 Stickers and GIFs (`Application/Stickers`, `ApiRoutes.Stickers`)

No stock catalogue and no third-party GIF search (privacy, blocks in Russia, licensing): stickers and GIFs come from
the users themselves, and there is no built-in pack.

- **How they travel** (compatibility with apps before stickers, 0.2.61): a sticker is a `Photo` message whose single
  media carries `MediaDto.Sticker` (`StickerInfoDto`: pack, sticker, main emoji); a GIF is a `Video` whose media has
  `MediaDto.IsAnimation`. Both are additive JSON fields: older apps skip them and show a plain photo (a WebP) or a plain
  video, so no `MessageType`, `MediaKind` or `TypingAction` value was added and `Server:MinClientVersion` stays.
  `DeepLinkKind.StickerPack` is app-side only (older apps leave such links alone).
- **Packs**: `POST api/stickers/packs {Id, Title}` (the app picks the id: a repeat returns the pack; someone else's id →
  409; title 1..`StickerPackTitleMaxLength` = 64 after `StickerRules.NormalizeTitle`, else 400; ≤ `MaxOwnStickerPacks` =
  100 per user → 400 `sticker_pack_limit`) makes a pack of mine with a random 10-character slug (`StickerRules.NewSlug`,
  the link `/addstickers/{slug}`), installed for me first. `GET api/stickers/packs/{id}` and `.../by-slug/{slug}` (any
  signed-in user; 404 `sticker_pack_not_found` once deleted). Only the creator renames (`PATCH`), adds (`POST
  .../stickers {Id, MediaId, Emoji}`), changes a sticker's emoji (`PATCH .../stickers/{id}`), deletes a sticker,
  reorders (`PUT .../order`, `StickerRules.Arrange`) and deletes the pack (204); anyone else → 403
  `not_sticker_pack_creator`. A sticker is made from an upload of the creator (`MediaKind.File`, the original bytes; 404
  `media_not_found` otherwise): `StickerImageProcessor` (SkiaSharp) accepts only PNG, WebP and JPEG by their bytes, ≤
  `StickerSourceMaxBytes` = 5 MB, each side ≥ `StickerSourceMinSide` = 64 px (else 400 `sticker_image_invalid`), applies
  EXIF orientation and stores a lossy WebP with its alpha plane, ≤ `StickerMaxSide` = 512 px (never enlarged), as a
  media of its own (Photo, `image/webp`, a ≤ 256 px WebP thumbnail, no mini thumbnail, encrypted at rest like any
  media, the sticker attributes on it); the upload is then deleted when nothing else uses it. 1..`StickerEmojiMax` = 8
  emoji per sticker (`StickerRules.NormalizeEmoji`: each exactly one emoji, duplicates by `EmojiKey` dropped; the first
  is the main one, kept on the media too); ≤ `MaxStickersPerPack` = 120 → 400 `sticker_pack_full`. Deleting a pack
  deletes its stickers, installations, favorites and recent uses; their media stay for the messages that show them
  (media cleanup takes the rest), and the media keep their sticker attributes, so sent stickers keep showing.
- **My state** (`StickerStateDto`, `GET api/stickers`): installed packs in my order (refs with title, version, count
  and cover), packs I made (newest first), favorites (≤ `MaxFavoriteStickers` = 50, newest first; past it the oldest
  goes), recent stickers (≤ `MaxRecentStickers` = 20, by my sends) and saved GIFs (≤ `MaxSavedGifs` = 200, newest
  first, their media without the mini thumbnail). `PUT/DELETE api/stickers/installed/{packId}` (a new one goes first;
  ≤ `MaxInstalledStickerPacks` = 200 → 400 `sticker_pack_limit`), `PUT api/stickers/installed/order`, `PUT/DELETE
  api/stickers/favorites/{stickerId}`, `POST api/stickers/gifs {ChatId, MessageId}` (the message's ready animation, a
  message I can see; from a chat with content protection 403 `forward_restricted`; not a GIF → 400), `DELETE
  api/stickers/gifs/{mediaId}`. Like the folders: every change runs under my `sticker_preferences` row lock (created by
  `INSERT … ON CONFLICT DO NOTHING`), a real change bumps the revision and sends the whole state to all my devices as
  `StickersChanged`, a change that changes nothing is silent, and every call answers with the state. A pack's edit
  bumps its `Version` and the revision of everyone it concerns (the creator, who installed it, who keeps a removed
  sticker among favorites or recent ones) and sends them their states (connected ones; the others load it when they
  connect). Lock order: the pack row first (an edit `FOR UPDATE`, a user's change that adds rows pointing into packs
  `FOR SHARE`), then the users' rows in id order — so a pack edit never deadlocks with its users' own changes.
- **Access**: anyone signed in sees and installs any existing pack and its images; sending a sticker needs a sticker of
  an existing pack; re-sending a saved GIF needs its media still visible to me (a message I can see — when its message
  is deleted for everyone and I have no other copy, it can no longer go out, and the app offers to remove it).
  Content protection blocks «Сохранить в GIF» and adding a protected chat's sticker to favorites (the apps hide them),
  like forwarding; opening and adding the pack stays allowed. Deleting an account removes its installations,
  favorites, recent stickers and saved GIFs; the packs it made stay for whoever installed them.

## 7. Realtime event matrix

| Event | Sent to | When |
|---|---|---|
| MessageCreated | chat members | send, forward, system message, call log |
| MessageUpdated | chat members | edit, reaction, pin/unpin |
| MessagesDeleted | members (forEveryone) / caller (for me) | delete |
| MessagesRead | chat members | read up to seq |
| Typing | other members (not blockers) | hub SendTyping |
| ChatAdded | the user(s) | new group, added, joined, first private message, unhide |
| ChatUpdated | members | title/description/avatar (and its history)/permissions/member count/active call/content protection |
| ChatRemoved | the user(s) | left, removed, group deleted |
| ChatMembersChanged | members | add/remove/role |
| ChatSettingsChanged | caller's user; a member whose archived chat a new message brought back | pin/archive/mute/contact bar closed |
| PinnedChatsReordered | caller's user (all devices) | pinned chats rearranged (`PUT api/chats/pinned-order`) |
| ChatFoldersChanged | caller's user (all devices) | folders, folder pins or chat list settings changed (whole state, by revision) |
| StickersChanged | the user's devices; everyone a pack edit concerns | installed packs, their order, favorites, recent stickers, saved GIFs, or a pack I use edited or deleted (whole state, by revision) |
| HistoryCleared | caller / both | clear history |
| PinnedMessagesChanged | members | pin/unpin |
| PresenceChanged | related users (per-viewer privacy, not blocked) | online/offline |
| UserChanged | related users + the user's own devices | profile/privacy/phone/avatar (and its history)/birthday/color/cover/links/blocks |
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
  network errors keep the session. `UserChanged` for me → `RefreshMeAsync`. A sign-out is the user's choice when this
  device revokes the session (`LogoutAsync()`: "Sign out", "Forgot the passcode?", switching to another server) or
  with `byChoice` (deleting the account: `LogoutAsync(false, byChoice: true)`, the server already ended the session at
  the user's request); it first runs `AccountLifecycle.OnSignOutByChoice` — also when the server's own sign-out
  (`SessionRevoked`, sent before the answer to the deletion) is running or has just finished, then for the account that
  sign-out ended — and, while the session still works, `OnBeforeSignOut` (calls hang up) before the server is told.
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
- **IChatFolderStore** (folders): restored from the cache ("folders"), loaded at sign-in and on every resync (which also
  retries a first load that failed: an offline start, a server restarting; a load that fails while the connection comes
  back runs once more; Settings › Folders asks again too); keeps the
  newest server state by revision (a first server state after a cache restore always wins: the server may have been
  reset) and lays the changes still on their way on top of it (`ChatFolderEdits`, the server's own edits), so another
  device's change never makes a pending one flicker; a refused change is put back and rethrown. `CreateAsync` makes a
  folder, `SaveAsync` edits one (a folder deleted elsewhere is not brought back, locally or on the server),
  `UpdateSettingsAsync` sends only the settings it changes. `ChatFolderView` shows a folder (its pins first, then by
  activity; an "unread" folder keeps the chat open next to the list, or on phones the one last opened from it, until
  another is opened or the folder switched) and counts unread messages for the tabs (`BadgesOf`: every folder in one
  pass), and `ChatFolderPresets` are the recommended folders. Counters on the chat list count unread messages like the
  dock: the dock = everything not muted, "All chats" = the main list, the archive row = the archive, each folder = its
  chats (muted ones only when nothing else, shown grey). `ChatBehaviorSettings.ChatSwipe` (per device): Auto = switch
  folders once there are folders, else the chat actions.
- **IStickerStore** (stickers and GIFs, `Stores/StickerStore.cs`): the folder store's pattern — restored from the cache
  ("stickers"), loaded at sign-in and on every resync, newest server state by revision with my pending changes laid on
  top (`StickerEdits`), a refused change put back and rethrown. The stickers of installed packs are kept by `Version`
  ("sticker-packs") and fetched again when a state names a newer one; `LoadPackAsync`/`LoadPackBySlugAsync` for a pack's
  sheet; `Suggest(emoji)` = the installed packs' stickers whose emoji match (`StickerRules.Matches`: variation selectors
  and skin tones ignored), in my order. `AddStickerAsync` uploads the picture as a `MediaKind.File` (its original
  bytes) and adds it. `IMessageStore.SendStickerAsync` / `SendGifAsync` go through the outbox like text (nothing to
  upload; the pending bubble shows the media itself; a queued one survives a restart with its sticker / media id).
  Settings (per device): `ChatBehaviorSettings.SuggestStickers` and `AutoplayGifs` (both on). GIFs follow the photos'
  auto-download switch (`MediaDownloadService.ShouldAutoDownload`): they are small loops shown like pictures.
- **ICallService**: per its doc. Before starting/accepting request Microphone (and Camera for video) via
  `IPermissionService`; call `ICallPlatformService.OnCallStartedAsync` only while in foreground. Incoming
  while in a call → `DeclineCallAsync(busy: true)`. Outgoing gets `JoinInfo` immediately. Ended calls stay 2 s with `EndStatus`. `ICallPlatformService` start/media/end;
  its `CommandRequested` and `ILocalNotificationService.CallActionInvoked` feed `RequestCommand`/accept/
  decline. Incoming in background → `ShowIncomingCallAsync`.
- **INotificationCoordinator** per its doc; badge = `TotalUnread` via `SetBadgeCountAsync`; notification taps
  open `DeepLinks.AppChatUri(chatId)`. While the app passcode locks the app, messages notify through the system
  even in foreground (no in-app banner); with "hide notification text" the notification says only
  "SkyMessage: New message" (`LocalNotification.Hidden`: the chat's earlier lines go too), and the moment the lock
  engages every message notification already on screen gets the same text (`ILocalNotificationService.RedactMessagesAsync`:
  Android re-posts them silently, also those an earlier run of the process left in the shade, and the summary stops
  naming chats; browsers re-show them by tag; iOS replaces them with one per chat).
- **IAppLockService** (app passcode, "код-пароль"; rules in `AppLockRules`, per device and for the whole app): a 4-
  or 6-digit code; only a salted PBKDF2-HMAC-SHA256 hash is stored (`PasscodeHasher`: 16-byte salt, rounds calibrated
  when the code is set to ≈ 0.25 s per check on this device, 10,000–600,000, kept with the hash; the browser derives
  it with WebCrypto through `IAppLockPlatform.DeriveKeyAsync`, the same bytes as .NET's) in `ISecureStore`
  ("passcode.v1" with the options, "passcode.attempts.v1"), plus a plain marker that one is set ("passcode.set.v1" in
  `IPreferencesStore`; written after the record, removed before it). Reads use `ISecureStore.TryGetAsync`, which
  reports a failed read instead of wiping the store; a store that cannot be read, a damaged record, or a record gone
  while the marker says one was set (Android's secure storage drops what it cannot decrypt) all lock with no code
  matching — only signing out removes them. With a passcode: every cold start is locked (`ClientBootstrapper` reads it
  before the first screen, also when loading the settings or restoring the session failed; the UI's start fallback
  reads it too, and the shell stays covered until it has been read: `IsInitialized`); leaving the app
  (`IAppLifecycle`) for longer than the auto-lock delay locks it — immediately / 1 min (default) / 5 min / 1 h / 5 h —
  measured with the wall, process and boot clock (`IAppLockPlatform.SinceBoot`): sleep counts, a wall clock moved back
  locks (without a boot clock) and never helps; a check every 5 s while away locks before the user is back, so
  returning never shows chats for a moment; "immediately" locks as the app leaves, except for the app's own
  pickers/camera/system screens (`ExpectReturn`: a trip announced within `AppLockRules.OwnTripWindow` (1 min) of
  leaving gets `OwnTripGrace` (2 min); it covers one leave and ends when the app is back, so a picker result that
  never came cannot soften the auto-lock later; the browser's file pickers announce it too, since a phone browser
  hides the page while its picker or camera is up); `LockNow`. Five wrong codes in a row pause the checks for 30 s,
  each further one doubles the pause up to 1 h; with a boot clock the wall clock is not used for the pause at all —
  after a phone restart only the time since that restart counts (the pause is re-anchored to it) — so moving the clock,
  restarting the app or the phone never shortens it; the count survives restarts, is shared by browser tabs and starts
  over after a right code or fingerprint. While locked the window is secure (`IScreenCaptureGuard`); while a passcode
  is set the recents picture shows no content. Change and turn off need the current code. Signing out by choice
  ("Sign out", "Forgot the passcode?", switching to another server, deleting the account:
  `AccountLifecycle.OnSignOutByChoice`) removes the passcode before the session goes, the app staying locked until it
  has; a sign-out the user did not ask for (the session expired, was ended from another device or another server
  answered) keeps it for the account that set it (the record names it), nothing locks while signed out, and the next
  sign-in of that account with the password opens the app (the count starts over) with the passcode on again. Any
  other account never inherits it: its sign-in removes the passcode (nobody else knows the code), and so does a
  session of another account restored at start (one whose sign-in ended before the passcode was removed); a record
  that names no account (unreadable) is removed by the next sign-in with a password. Browsers:
  every tab is an instance of the app on the same storage (`IAppLockPlatform.Announce`/`Signaled`, web-platform.js
  `skyPlatform.passcode`: a BroadcastChannel, storage events). "Lock now" locks every tab, a right code or fingerprint
  in one opens all of them, turning it off opens them; turned on in another tab, a hidden tab locks at once and a
  visible one stays open; changes to the stored passcode reach the others at once and are re-read before every check,
  change and on the way back (a stale tab cannot set a second passcode over another's or write an old one back); a
  passcode that vanished elsewhere leaves a locked tab locked (that could be a sign-out there). Each tab measures its
  own time away; a new tab starts locked. Native apps have one instance and read the keystore at start only.
- **IBirthdayService** (`Services/Birthdays.cs`): contacts whose birthday (as their privacy shows it) is today
  (`Celebrating`, with the age when the year shows), the chat list's card closable for the day (`IAccountLocalStore`),
  `IsBirthdayToday(user)` for the 🎂 next to anyone's name, and reminders at 10:00 local time for the next 14 days
  through `IReminderNotificationService` (one per person, linking to their chat or profile; scheduled again at every
  start, on contact/birthday/setting changes and after midnight; nothing with
  `NotificationSettings.BirthdayReminders` off; people I blocked get no card, reminder or 🎂). Each reminder carries
  its day (`ScheduledReminder.Day`): one the OS delivers on another day or more than `ReminderRules.MaxDelay` (6 h)
  late is dropped. After midnight the reminders go to the OS again even when unchanged. Texts from the UI's
  `IReminderTexts`: "Сегодня день рождения у Анны" (`RussianNames.Genitive` declines given names, family words,
  patronymics and common surnames, and keeps names Russian does not decline as they are — "у Нелли", "у Седых", "у
  Мариам Петровой"; a word in a consonant on its own only when it is a man's name or a word like "Брат", since
  "Ковальчук" may be a woman's; a name it cannot decline for sure follows a colon instead: "Сегодня день рождения:
  Саша работа").
- **IContactSyncService** (`Services/ContactSync.cs`, `ContactsSettings.SyncPhoneContacts`, per device): with the
  contacts permission (never asked for by itself — only when the user turns it on) it reads the phone book at sign-in
  / app start and on `IDeviceContactsWatcher.Changed` (5 s after a burst, and at least 30 min after the previous
  sync). The numbers go to find-by-phones only when their set changed (a hash per account) or a day has passed, and
  never during a pause after a 429 (the server's window plus 30 s; a sync is scheduled for its end, and no sync is
  ever scheduled sooner than 5 s ahead). A sync the network or the server cut short (no connection, a timeout, 5xx)
  tries again 15 min later, the wait doubling with each failure in a row up to a day (never before a pause ends). They go in key order, 1000 per request and at most 5 requests per sync (half
  the window, the import shares it); a look-up cut short by that or by a 429 keeps the people found so far and goes on
  with the other numbers after the pause, so a phone book of any size finishes. It adds the
  people found as synced contacts; synced contacts whose number left the phone book are removed (not when the phone
  book reads empty or more than half would go at once) and renamed ones take the new name — the user's own contacts
  are never touched. It announces a newcomer once ("X теперь в SkyMessage", `IReminderNotificationService`) only when
  their number was in the phone book at the previous look-up, at most 14 days ago; the first sync and the first after
  turning sync on are quiet. Per account it keeps whom it found (someone removed by hand is not brought back), which
  number found each synced contact and the numbers of the last look-up — as short SHA-256 hashes, never the numbers.
  `RemoveSyncedAsync` deletes the synced contacts on the server, turns sync off and forgets all of it.
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

**Colors and backgrounds of one's own** (Settings › Appearance; per device, in `AppearanceSettings`, repaired by
`AppearanceRules`/`BackdropRules` on load and on every change):
- My bubbles (`OutgoingBubble`): the accent's gradient (default, as before), the accent flat, or my own colors
  (`OutgoingColors`: one = flat, two = a 135° gradient; `BubbleColorsDialog`). Their bubbles (`IncomingTint`): white/indigo
  ("default", unchanged) or a calm tint (lavender, peach, mint, sky, sand) light by day and deep at night. The ink on
  every bubble is derived and keeps **4.5:1** (WCAG AA; gradients are checked along their length): accent bubbles
  (gradient or flat) carry white ink like every accent fill, their stops deepened toward black where that is enough
  (at most 20%; `ThemeCatalog.ReadableFor`), and only an accent too light for that takes dark ink; my own colors take
  white or dark (#1C1634), whichever reads better, nudged just enough (`ThemeCatalog.Readable`). A stop that already
  reads well is left as it is. The time and ticks and a failed status on my bubble are solid colors that keep at least
  **3:1** on every part of it (`--sm-bubble-out-meta`, `--sm-bubble-out-danger`). Text on accent fills (buttons, badges,
  the send orb, the dock pill: `--sm-on-accent`) keeps the released rule: white unless the fill's middle is very light
  (luminance above 0.42), so custom accents saved before look as they did.
- The app's background (`Wallpaper`): a sky, or "custom" = `CustomBackground` — a gradient (2–3 colors, any angle,
  ready-made ones offered) or a gallery photo with blur and dim (a veil of the theme's background color), one for
  both themes or one for each (`SameForBoth`; a first one starts per theme). Kept while a sky is chosen. Behind every
  screen that shows the app's sky (root screens, pushed pages, the wide layout, auth, boot splash).
- **Readability floor**: root screens put text straight on the app's background, so one's own background is veiled
  toward the theme's background (`--sm-bg`) as much as that text needs — titles and names (`--sm-text`) 7:1,
  previews and footers (`--sm-text-2`) 4.5:1 and, at night, the chat rows' times and the section overlines
  (`--sm-text-3`) 3:1 (about 4:1 on the night's own sky; by day `--sm-text-3` stays under 3:1 even on the plain
  background, so it gets what the other two leave) — checked everywhere on it (`ThemeCatalog.ScreenFloorOf`,
  `ScreenVeil`). A gradient is veiled as a whole in the theme CSS (`ScreenGradient`; colors that suit the theme stay
  as they are); a photo is measured by boot.js on the part the window shows (8×8 cells) and veiled row by row
  (`--sm-sky-veil`, never less than the user's dim; `ScreenVeilRows`), so a light sky over dark hills keeps its sky.
  Chats that follow the app look the same; a chat's own background has no floor (its text sits on bubbles and glass).
  The editor says when the floor dims a theme (a note only: its mocks already show the dimmed result and the chosen
  colors stay as they are) and lists the ready-made gradients that suit the edited theme first. Over one's own
  backgrounds no sky lights, clouds or stars are drawn (`html[data-sky-own]`, `SkyBackdrop Lights`), and the passcode's
  lock screen shows the theme's calm sky (the default one, `--sm-calm-*`) instead: its rose hint and its link need the
  sky's contrast. The system bars follow what lies under them: the top of the app's background (the browser's
  theme-color too), or a chat's own background while it is on a phone's screen (`ThemeService.HoldSystemBars`).
- The chats' background (`ChatBackground`, null = the app's) and one chat's (`ChatBackgrounds[chatId]`, from the chat
  header menu, "Фон чата"): a sky, a gradient or a photo; "reset" goes back to the default. Single chats' backgrounds
  belong to the account: a chat that is gone for me (`ChatRemoved`, or missing from a full chat list after an offline
  stretch: `IChatListStore.Synced`) drops its background, and signing out (or another account signing in) drops them
  all; the app's and the chats' backgrounds stay with the device. Appearance › Reset keeps single chats' backgrounds.
- Photos are downscaled to the screen (`IMediaCompressor`, metadata stripped) and kept per device by
  `IBackdropImageStore` (MAUI: files in the app's data dir served at `/_sm/backdrops/{file}`; browser: IndexedDB
  "skymessage-backdrops", records `{ type, blob, savedAt }`); `BackdropPhotoService` deletes a photo once no setting
  refers to it (replace, reset, cancelled editor, leftovers after start — not ones stored within the last hour, which
  another tab's open editor may hold). Pictures are always still (drawn once onto a canvas: a GIF shows its first
  frame) with the blur baked in by boot.js, which shows the persisted theme's pictures (`ThemeState.Images`) right
  after the first paint; gradients are in the theme CSS itself. Lite mode changes nothing about them (they never move).
  Editors preview both themes side by side (`BackdropEditor`, `BackdropPreview`).

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
previews). `ThemeService` calls `ISystemUiService.SetSystemBarsStyleAsync` on theme changes (dark or light icons
for what lies under the bars; a chat with its own background holds them while on screen); tap feedback
uses `IHapticsService` when `InAppVibrate`.

**Routes**: `/`, `/welcome`, `/login`, `/register`, `/chat/{ChatId:guid}`, `/chat/{ChatId:guid}/info`, `/addstickers/{Slug}`,
`/chat/{ChatId:guid}/media`, `/user/{UserId:guid}`, `/u/{Username}`, `/join/{Token}`, `/contacts`,
`/contacts/import`, `/new-group`, `/search`, `/calls`, `/settings`, `/settings/{Section}` (the folder editor is
`/settings/folder?id=`).

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
(`Tones` catalog: packs "wood"/"bells"/"piano", each with message tones "chime"/"pop"/"glass", ringtones
"classic"/"soft"/"bright", ringback, busy, call-end, plus the user's own sounds; `PlayMessageTone(key)`,
`StartRingtone(key)`, `StartRingback()`, `Stop()`), `FormatService`
(times, durations, sizes, last-seen phrases), `IMessagePreviewFormatter`, JS wrapper for `app.js`), and
Avatars and profiles (Features/Profile): `AvatarOverlays` (scoped; `AvatarOverlayHost` in MainLayout) opens the
**avatar viewer** from a profile, group info or the chat header — the media viewer's night and gestures (viewer.js:
grows out of the tapped avatar, pinch/wheel/double-tap zoom, swipe through the history, swipe down to close), my own
photos and a group's I may change: set as main, delete — and the **crop editor** (avatar.js: squircle preview over a
dimmed veil, drag/pinch/wheel/slider zoom 1–5×, quarter turns, flip; Done draws a 1024 px JPEG on the device).
`AvatarFlows` turns gallery/camera picks (pre-shrunk to 2048 px by the device's compressor, HEIC included) and the
emoji maker (`EmojiAvatarDialog`: an emoji on an `AvatarPalette` gradient) into prepared photos that
`AvatarUploader` sends as they are. `ProfileLook` maps profile colors to the accent pairs (ring, main action with the
color's own ink — the same for every viewer —, a soft cover wash, sender names in groups) and cover keys to calm skies
for both themes. Settings › Profile edits links,
color, cover (gradients or a photo) and the birthday (`BirthdayDialog`); Privacy has the Birthday level;
Notifications the birthday reminders switch; the chat list shows `BirthdayCard` (faces, one line, one "Поздравить" —
with several people it opens a chooser with their full names; second in the chat list's notice slot, which shows one
card at a time: update, birthday, notifications); the phone-book page offers sync.

features: Auth (welcome with server picker + connection test, login, register with live username check
and invite code when required), ChatList (avatar, title, preview incl. "Draft:", sender for groups, time,
unread + mention badges, pinned/muted marks, typing preview, actions pin/mute/archive/delete, archived
folder; chat folders: pill tabs under the header with a gliding pill and unread messages per folder (with them the
scrolled header is one pane of glass), a long-press menu (mark all read, edit, move left / right, delete; "All chats":
mark all read, open the archive, set up folders), a folder's tab lifted by the long-press (or dragged with the mouse)
moves sideways to reorder the folders, arrow keys between the tabs, a vertical mouse wheel scrolls the strip, a sideways
swipe on the list that follows the finger with the neighbouring folder beside it when "Swipe on a chat" says so, Back
on phones returning to "All chats" first, each folder's own pins, an empty-folder state; the archive row lives in "All
chats" or, hidden, behind a header button), Contacts (online first, add by username, phone-book import with confirmation list showing
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
sessions, the app passcode — `/settings/passcode`: turn on with a 4- or 6-digit code entered twice, change, auto-lock
delay, fingerprint/face unlock (shown where `IAppLockPlatform.SupportsBiometrics`, confirmed by the system prompt once),
hide notification text while locked, lock now, turn off; the code steps run in `Features/Passcode/PasscodeDialog`;
where a passcode blocks screenshots (`IAppLockPlatform.BlocksScreenshotsWhileSet`: Android 8–12) both states say so —,
change password, delete account (then signs out by choice: `LogoutAsync(false, byChoice: true)`), sign out),
notifications & sounds (toggles incl. keep connected
in background + battery exemption via `IBackgroundConnectionService`; on iOS a hint that the app must stay
open to receive calls until push lands; sound pack cards, tone pickers and the user's own message tone / ringtone, with previews through `ISoundService`), appearance (theme, accents, message colors (my bubbles' style, their tint), the
app's background (skies + "Свой": gradient or photo, per theme or both) and the chats' background with a live preview, font size, bubble radius,
animations, compact list; see "Colors and backgrounds of one's own" above), chat settings (send by Enter, quick reaction via `EmojiPicker`, autoplay, voice
speed), **folders** (`/settings/folders`: my folders — "All chats" first, reorder by the handle, long-press menu —,
recommended folders added with one tap, "Swipe on a chat", new chats from strangers, the archive; the editor
`/settings/folder?id=`: name, icon or emoji, types, chats included/excluded through `ChatPickerDialog`, exclusions,
a live count, delete; Save and delete wait for the server, so a refused save keeps the draft, and a folder deleted on
another device meanwhile is not brought back — the editor offers to save the draft as a new folder), **devices** (mic select + live level meter + record/playback test; camera select + preview +
mirror; speaker select + test tone (`setSinkId` where supported); echo cancellation / noise suppression /
auto gain; call video quality; phones: default audio route via `IAudioRouteService`; Android: pause music in other
apps during calls and note recordings via `IAudioFocusCoordinator`, default on), data & storage
(auto-download Wi-Fi/mobile, photo/video quality, save to gallery, cache size + clear via
`IMediaDownloadService`, low data mode), language, connection (server + status/transport, test, custom
server, fallback addresses, proxy HTTP/SOCKS5 with test via `IServerEndpointService.TestAsync`, hidden when
`IHttpHandlerFactory.SupportsProxy` is false; text: "The proxy applies to messages and media; calls may not
work through a proxy"), about (version, server version, licenses of everything
shipped). Calls: `CallOverlay` (incoming screen with accept audio/video / decline + ringtone; outgoing with
ringback; 1:1 layout (remote full, draggable local PIP) and group grid with active speaker; a shared screen takes
the stage with the people (presenter first) in a strip, 1:1 and groups (several at once: the newest share, others
marked in the strip and a tap away, `CallScreenShares`); deck in this order: audio output, camera,
microphone, hang up (flip in the top bar, screen share on desktop in a capsule after the deck; phones only view
shared screens); minimize to a top bar; timer; quality;
reconnecting; ended screen; when the local camera track ends/mutes after the app returns from background,
offer to re-enable it) executing `ICallService.CommandRequested` and reporting `Report*`;
`GroupCallBanner`; `/calls` history (icons, call back, missed filter). `calls.js` wraps **livekit-client**
(vendor the UMD build of npm `livekit-client@2.22.4` + LICENSE into `wwwroot/lib/livekit/`), maps
participants by `metadata.userId` (identity is per device), reports through a `DotNetObjectReference`.
`devices.js` serves both settings and calls.

**App passcode** (`Features/Passcode`): `LockScreen`, rendered once by `MainLayout`, covers the signed-in shell while
`IAppLockService.IsLocked` (z-index 990: over dialogs, menus, banners and toasts), and — without a keypad — while the
stored passcode is still being read. The shell stays mounted underneath, `inert` with its animations paused, and every
`ShellContext` reports `IsCovered` (no read marks, autoplay or active chat; notifications go through the system). The
calm sky (the theme's default one over a background of one's own; the hint and the link use `--sm-danger-text` and
`--sm-accent-text`, 4.5:1 on it), the logo mark, "Enter your passcode", the dots and our own glass keypad (`PasscodePad`, a fresh one for
every lock; no system keyboard; on a computer digits and Backspace type through `js/passcode.js`, Escape does not reach
the app underneath); wrong codes shake the dots (rose) with a haptic, the hint says how many tries are left before a
pause, the pause after five counts down on the hint line; the fingerprint key and a one-time automatic prompt per lock
when allowed (not while a call rings or is live, where it would cover the call screen: it then waits for the next
return to the app); "Forgot the passcode?" confirms in place and signs out of this device (`IAuthService.LogoutAsync`:
local data cleared, passcode removed); Android Back and Escape close that question (`LockUi.TryBack`). An active call
stays reachable: "Back to the call" expands the call screen, which goes above the lock while locked (an incoming call
opens it by itself), with the toasts it raises above it (`shell--call-over-lock`); minimizing it shows the lock again.
Android Back on the lock screen otherwise moves the app to the background.

**Stickers and GIFs** (`Features/Stickers`, §6.6): the composer's emoji button opens `ExpressionPanel`, «Эмодзи ·
Стикеры · GIF» with the switch at the bottom (the last tab remembered per device). Stickers: a strip of «Недавние»,
«Избранные», every installed pack's face (its first sticker), «Создать набор» and the settings; then all of them in one
scroll with sticky name chips. A tap sends at once (as a reply when one is set); a hold or right-click (`js/stickers.js`
`attachHold`, one delegated listener) opens `StickerPreviewDialog`: the sticker large with favorite / send / its pack.
GIF: «Мои GIF» as still first frames; a tap sends one again, a hold offers send or remove; one that can no longer be
seen (its message deleted) shows as unavailable and offers removal. With exactly one emoji in the field
(`SuggestStickers`), `StickerSuggestions` floats the matching stickers over the composer; a tap sends one in its
place. A sticker message has no bubble: the picture at about 160 px (180 on wide screens), sender / forward / reply as
pills above it and the time on a chip below (like round notes); a tap opens `StickerPackSheet` (`StickerPackView`:
title, count, every sticker, «Добавить N стикеров» or «Удалить набор», «Изменить» for its creator, share the link; a
deleted pack says so). The message menu adds favorite (not in protected chats) and «Набор стикеров»; stickers are not
editable and stay out of the media viewer's sequence. A GIF tile loops muted while on screen (`MediaTile`, player.js
autoplay) with a «GIF» mark; with `AutoplayGifs` off, in lite mode or with reduced motion its first frame waits for a
tap, the next tap opens the viewer, which loops it muted too; the menu has «Сохранить в GIF» (not in protected chats)
or «Убрать из «Моих GIF»». Previews: «Стикер 😺» (its main emoji), «GIF». `/addstickers/{Slug}` (`StickerPackPage`)
shows the same view for pack links (remembered through sign-in like invite links). Settings › «Стикеры и GIF»
(`/settings/stickers`): the two switches, my packs and «Создать набор», the installed packs in the panel's order (moved
by their handle, `attachHandleReorder`; the bin removes one with Undo), how many GIFs I keep; the editor
(`/settings/stickerpack?id=`): rename and share in the header, stickers as rows (handle; a tap changes the emoji or
deletes), «Добавить стикеры» from the gallery or files — each picture in `NewStickerDialog` (its picture as it is,
problems named: not PNG/WebP/JPG, under 64 px, over 5 MB; «Обрезать квадратом» = the avatar crop editor's sticker mode,
`AvatarOverlays.EditStickerAsync`: a square window and a transparent 512 px PNG; its emoji, at least one) — uploaded one
after another with their progress, then «Удалить набор». The chrome stays calm: no frames, glows or sparkles around
stickers; they are the content.

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
  REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, USE_FULL_SCREEN_INTENT, USE_BIOMETRIC, WRITE_EXTERNAL_STORAGE maxSdkVersion 28;
  `allowBackup="false"`; `<queries>` for IMAGE_CAPTURE, VIDEO_CAPTURE, VIEW (https/mailto/tel) and SEND;
  DEBUG-only `UsesCleartextTraffic`; `skymessage://` and notification deep links. Call service type derived
  from granted permissions (microphone; camera added only in foreground); Accept/AcceptVideo notification
  actions are Activity PendingIntents (bring MainActivity forward, then start the service); Decline/Hang up
  may be broadcasts. Notification sounds: our synthesized packs as Ogg Vorbis in Resources/Raw
  (tone_<pack>_<key>, written by `tools/tones`), or the user's own file through a FileProvider; one channel per
  sound (recreate on change); incoming calls ring with FLAG_INSISTENT or a
  looping ringtone stopped on accept/decline/foreground.
- Apple: Mac `Entitlements.plist` camera, audio-input, addressbook, user-selected + downloads read-write,
  `keychain-access-groups` = `$(AppIdentifierPrefix)com.skymessage.messenger`; iOS Entitlements with the
  keychain group (simulator); Info.plist NSCamera/NSMicrophone/NSContacts/NSPhotoLibraryAdd/NSLocalNetwork
  usage strings, UIBackgroundModes audio; set `UNUserNotificationCenter.Current.Delegate` in
  `FinishedLaunching` before base; `ISecureStore` catches Keychain errors (degrades in DEBUG). Caches and
  the outbox under Library/Caches (excluded from backup).
- Platform services (Android complete; iOS/Mac compiling with best-effort implementations;
  `IsSupported=false` where impossible): PlatformInfo (+ CountryCode from SIM/network/locale), SecureStore,
  PreferencesStore, LocalCacheStore (files), OutboxFileStore, BackdropImageStore (background photos in the app's data
  dir, answered on the app origin at `/_sm/backdrops/{file}` next to the media proxy), FilePicker (mixed photo+video selection
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
  CallPlatformService (foreground service microphone|camera, wake lock, proximity screen-off lock only while call
  audio is on the earpiece in communication mode with my camera off, screen kept on while the call shows a picture
  (`CallScreenPolicy`), ongoing-call notification with Mute/Hang up → `CommandRequested`), AudioFocusService
  (transient focus for ringing, calls, recordings), BackgroundConnectionService (low-priority persistent foreground
  service + battery exemption prompt), NetworkStatusService (Connectivity), SystemUiService, HapticsService,
  ShareService, FileSaveService (+ gallery), FileOpener, MediaFileStore (cache dir), AppLifecycle,
  AppLockPlatform (app passcode: framework BiometricPrompt on Android 10+ with weak biometrics so face unlock counts,
  USE_BIOMETRIC; no recents picture while a passcode is set — `setRecentsScreenshotEnabled(false)` on Android 13+;
  below, a window that stays FLAG_SECURE for as long as a passcode is set (those versions take the picture before an
  app can react — with gesture navigation while it is still resumed — and leave out only a window that was secure all
  along), so the app cannot be screenshotted meanwhile and the passcode settings say so
  (`IAppLockPlatform.BlocksScreenshotsWhileSet`); `SystemClock.elapsedRealtime` as the boot clock; Back on the lock
  screen closes its "Forgot the passcode?" question, else leaves the app unless the call screen is over it; shown over
  the phone's own lock screen for a call, the app locks (`LockNow`) whenever the activity starts or resumes over a
  locked keyguard — also for a call accepted while the phone was unlocked and locked during it — so minimizing the call
  never shows chats there; `ActivityResults` launches, camera capture and the own-sound picker announce
  `ExpectReturn`; iOS/Mac: keypad only),
  ReminderNotificationService (Android: "Reminders" channel; future ones as alarms allowed while idle —
  `setExactAndAllowWhileIdle` on Android 8–11, where it needs no permission and an inexact alarm could come days late,
  `setAndAllowWhileIdle` on 12+, at most an hour late — whose receiver posts the stored text unless it is late by
  hours or on another day (`ReminderRules.IsDue`), re-armed after reboot/update by `ReminderBootReceiver`, a reminder
  missed while the phone was off included — one whose alarm already went off leaves the store then and is not shown
  again; elsewhere unsupported — the app reminds on the chat list),
  DeviceContactsWatcher (Android: a ContentObserver on the contacts provider while sync is on; elsewhere unsupported),
  ExternalLauncher, DeepLinkSource (intents/URL opens/notification taps), InterProcessLock (in-process),
  AudioRouteService (route ids: `AudioDeviceInfo.Id` on API 31+, speakerphone/SCO below; AVAudioSession on
  iOS; re-apply the preferred route after publish and on device changes; `CallRoutePolicy`: a choice made in the
  call, else a connected headset, else the setting, else earpiece for voice and loudspeaker once the call shows a
  picture — an audio call moves to the loudspeaker once when my camera or someone's camera/screen appears on the
  open call screen, which also releases the proximity lock), HttpHandlerFactory
  (`SocketsHttpHandler` with Happy Eyeballs `ConnectCallback` (IPv6 first, IPv4 after 250 ms), HTTP/SOCKS5
  proxy, per-attempt connect timeouts, pooled lifetime 5 min; iOS: `NSUrlSessionHandler` when no proxy (or
  iOS 17+ proxy configurations); `CreateProxy` and `CreateWebSocketConnector` for SignalR; Android WebView
  `ProxyController` (androidx.webkit) for HTTP proxies, answer 407 in the wrapped WebViewClient; SOCKS with
  credentials cannot apply to the WebView — warn; WebRTC media never uses a proxy).
- Native strings via `ILocalizer` with `Localization/*/platform.json`.
- Replace the template icon/splash/titles with our own («Горизонт» from `docs/brand`: Android adaptive + themed icon,
  splash, notification icon; one full-bleed iOS / Mac Catalyst icon).
- Web host (`SkyMessage.Web`): `UseHostOrigin`, optional `ServerUrl` in `wwwroot/appsettings.json` for
  standalone dev, platform implementations through `wwwroot/web-platform.js` (IndexedDB cache + outbox
  blobs, IndexedDB databases of their own for the user's sounds and background photos, localStorage for preferences/secure store, hidden file input, canvas image compression,
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
channels, bots, animated stickers, polls, scheduled messages, link previews, 2FA, multi-account,
translation, certificate pinning, production deployment (Caddy TLS, TURN over TLS on 443 via SNI routing,
backups, separate prod compose with mandatory secrets), app links (`assetlinks.json`, AASA).
