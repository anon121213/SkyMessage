# SkyMessage UI kit — guide for feature screens

Practical reference for everything in `src/Client/SkyMessage.Client.UI` that ui-kit owns: the shell, kit
components, services, CSS tokens/utilities and JS helpers. ui-chat and ui-settings-calls build on it. Read
[SPEC.md](SPEC.md) §8.2 first; this file explains *how* to use the pieces.

Everything below is in namespaces already imported by `_Imports.razor`:
`SkyMessage.Client.UI.{Kit, Kit.Emoji, Services, Shell}`, `SkyMessage.Client.UI.Features.{Auth, Calls, Chat, ChatList,
Contacts, Groups, Profile, Search, Settings}`, all `SkyMessage.Client.Core.*`, `SkyMessage.Contracts`,
`Microsoft.Extensions.Logging`. `Features/Media` (ui-chat) is not imported (it did not exist yet): add
`@using SkyMessage.Client.UI.Features.Media` in your own files.

---

## 1. Rules of the road

| Rule | How |
|---|---|
| People | `Users.Get(id) ?? embedded` → `user.NameOf(L)`; or `Users.NameOf(id, L, embedded)`; render with `<UserAvatar>`, `<UserName>`, `<PresenceText>`. Upsert people you got from `ISkyApi` directly: `Users.Upsert(list)`. |
| Chat titles / avatars | `ChatList.TitleOf(chat)` / `ChatList.AvatarOf(chat)` (Saved is localized); render with `<ChatAvatar Chat="chat"/>`. |
| Media URLs | Only `IMediaUrlBuilder` (`ContentUrl(media)`, `ThumbnailUrl(id)`, `AvatarUrl(id)`, `MiniThumbnailDataUrl(media)`); re-render on `MediaUrls.Changed`. |
| Data | Prefer store methods (`IChatListStore`, `IMessageStore`, `IUserStore`, `IContactStore`…) over `ISkyApi`. Direct `IRealtimeService` subscribers must refetch on `Resynced`. |
| Core events | Fire on any thread → derive from `UiComponentBase` and use `RenderOn(...)` / `RequestRender()` (coalesced, safe after dispose). |
| Overlays | Every dialog/sheet/menu/viewer/selection mode owns a history entry → Back closes it. Use `Dialog`/`BottomSheet`/`ContextMenu`/`DialogService`, or `NavigationService.OpenOverlayAsync` for custom overlays (§4). |
| Navigation | Only through `NavigationService` (it tracks the back stack, closes overlays first, handles deep links). |
| Links | Never `MarkupString` for user content. Auto-links: `<LinkifiedText>` / `TextLinker`; open with `LinkService.OpenAsync(url, visibleText)`. |
| Strings | Every string via `L["area.key"]`; plurals `L.Plural("key", n)`; errors `L.Error(ex)` or `Toasts.Error(ex)`. |
| CSS | Tokens + utilities from `app.css` (global); component styles in `X.razor.css` (scoped, use `::deep` for child components). |
| Feedback | Errors → `Toasts.Error(ex)`; success → `Toasts.Success(text)`; destructive actions → `Dialogs.ConfirmAsync(..., danger: true)`. |

Localization key prefixes are owned per file (`Localization/{ru,en}/{area}.json`, flat keys): ui-kit owns
`app. common. nav. conn. date. duration. size. presence. typing. plural. message. theme. sound. emoji. kit. crash.
notFound. link.` (common.json), `error.` (errors.json), `auth. chatlist. contacts. profile. groups. search.`;
ui-chat owns `chat.` and `media.`; ui-settings-calls `settings.` and `calls.`; app-platform `platform.`.
Reuse kit keys freely (e.g. `common.cancel`, `common.save`, `common.copy`, `common.savedMessages`,
`message.preview.*`, `theme.accent.*`, `theme.wallpaper.*`, `theme.mode.*`, `sound.tone.*`, `emoji.category.*`,
`presence.*`). Russian is the primary language — write warm, plain wording; every key must exist in ru **and** en.

---

## 2. Hosting & startup

`UiSetup.AddSkyMessageUI()` (after `AddSkyMessageClientCore` and the platform services) registers:

| Lifetime | Services |
|---|---|
| Singleton (no JS) | `ILocalizer` (`Localizer`), `FormatService`, `MessagePreviewFormatter` (also as Core's `IMessagePreviewFormatter`), `ThemeCatalog`, `ToneCatalog`, `EmojiCatalog`, `EmojiRecents`, `RecentSearches`, `HapticFeedback`, `AvatarUploader` |
| Scoped | `AppJs`, `UiDispatcher`, `LayoutService`, `ThemeService`, `NavigationService`, `DialogService`, `ToastService`, `SoundService` (= `ISoundService`), `LinkService`, `ShareHelper`, `ChatActions` |

Host `index.html` (both hosts) must include, in this order:

```html
<link rel="stylesheet" href="_content/SkyMessage.Client.UI/css/app.css" />
<link rel="stylesheet" href="SkyMessage.App.styles.css" />           <!-- or SkyMessage.Web.styles.css -->
<script src="_content/SkyMessage.Client.UI/js/boot.js"></script>      <!-- BEFORE the Blazor script -->
<script src="_framework/blazor.webview.js"></script>                  <!-- or blazor.webassembly.js -->
```

`boot.js` must load before Blazor: it applies the persisted theme before the first paint and installs the
overlay-history `popstate` filter ahead of Blazor's router. Root component: `Routes` (`#app`).

`Routes.razor`: shows `BootSplash` → `IClientBootstrapper.StartAsync()` (once; idempotent) + `AppJs.InitAsync`
+ `LayoutService.InitializeAsync` → `ThemeService.InitializeAsync` → `NavigationService.InitializeAsync` (pending
deep link) → Router. Auth gate: pages without `[AnonymousPage]` require a session (signed-out users go to
`/welcome`, profile/invite/chat links are remembered and opened after sign-in); anonymous pages redirect
signed-in users. Layouts: `MainLayout` (signed in), `AuthLayout` (anonymous). Language change re-creates the
whole tree. An `ErrorBoundary` wraps everything (and each routed page inside the shell).

---

## 3. Shell

### Panes (`MainLayout`)

* **Phone (< 768 px)**: a *root* screen (Chats → `ChatListPanel`, Contacts → `ContactsPanel`, Calls → `CallsPage`,
  Settings → `SettingsPage` menu, all rendered by the shell itself) with the bottom `TabBar` on top-level routes
  (`/`, `/contacts`, `/calls`, `/settings`); every other route is a full-screen *page layer* sliding in from the
  right. The root shows the area of the top-level screen the history sits on (`/contacts` → profile → chat keeps
  the contacts list), so it **stays mounted** (scroll kept) under every page and Back reveals it as it was; the tab
  bar stays under the layers too. The chat (`/chat/{id}`) is its own layer and **stays mounted** under
  `/chat/{id}/info`, `/chat/{id}/media` and pages opened from it (profile from the header…), so Back returns to the
  same scroll position. Transitions: forward slides the page in over the previous screen (which drifts left and
  dims); Back slides the chat away and brings the screen underneath back. Every layer can be swiped away (below).
* **Wide (≥ 768 px)**: `IconRail` | list column (Chats/Contacts areas; resizable 300–480 px, remembered) | main
  pane | info column (≥ 1280 px) — `/chat/{id}/info` and `/chat/{id}/media` render there; between 768 and 1280
  px they float over the chat. The Calls and Settings areas hide the list column: your page gets the full
  width (use `.sm-split` for list + detail).
* On wide screens **MainLayout renders `ChatPage` itself** (not the Router) at a stable position, so the same
  instance survives info/media/profile navigation. `ChatPage` must therefore work from its `ChatId` parameter
  alone (it does get `[SupplyParameterFromQuery]` values — both hosts register the query provider).
* Area (`NavigationService.Section`): `/`, `/chat/*` → Chats; `/contacts*` → Contacts; `/calls*` → Calls;
  `/settings*` → Settings; other routes keep the current area.

### `ShellContext` (cascaded)

```razor
[CascadingParameter] public ShellContext? Shell { get; set; }
// Shell.Mode (Phone/Tablet/Desktop), Shell.Pane (Base, Page, Chat, Main, Sidebar, Info, InfoOverlay),
// Shell.IsCovered, Shell.IsPhone, Shell.IsWide, Shell.IsSidePane
```

**ChatPage rule**: when `Shell.IsCovered` is true the chat is hidden under another page — clear
`IChatListStore.ActiveChatId`, pause read tracking (`MarkReadAsync`), autoplay and video notes; resume when it
becomes false (parameters/cascade re-render tells you). The stub in `Features/Chat/ChatPage.razor` shows the
ActiveChatId part.

### Screen scaffold: `AppPage` / `AppBar`

```razor
<AppPage Title="@L["settings.privacy.title"]" Subtitle="…" BackFallback="/settings" Padded="true">
    <Actions><IconButton Icon="@IconNames.Search" Label="@L["common.search"]" OnClick="…" /></Actions>
    <Toolbar><SearchBox @bind-Value="_q" /></Toolbar>           @* optional, outside the scroll area *@
    <ChildContent> … sections … </ChildContent>
    <Footer><Button Block="true" Size="ButtonSize.Large">Save</Button></Footer>   @* optional, sticky *@
</AppPage>
```

| AppPage parameter | Meaning |
|---|---|
| `Title`, `Subtitle`, `TitleContent` | Header text, or custom header content (chat header: avatar + name + status). |
| `Back` (`bool?`) | null = automatic (hidden on top-level routes and in the list column; in the info column it becomes a close ✕). |
| `BackFallback` | Where back goes when there is no in-app history (deep link, reload). |
| `OnBack` | Custom back (wizard steps). |
| `Actions`, `Leading`, `Toolbar`, `Footer` | Slots. |
| `Scroll` (default true) | Body scrolls; `false` when the content scrolls itself (message list, pickers). |
| `Padded` | Centered 720 px readable column (settings-like screens). |
| `Background` | `PageBackground.Grouped` (grey behind cards, default) or `Surface` (lists). |
| `HideHeader`, `LargeTitle`, `Class`, `BodyClass` | — |
| `Body` (property), `ScrollToTopAsync()` | Scroll container access. |

`AppBar` has the same header parameters for screens with their own layout (ChatPage). Bottom insets are handled
(`--sm-page-bottom` / `--sm-bottom-space`); top/left/right safe areas are applied by the shell.

### Swipe back (phones)

Any phone page whose `AppBar` shows its back button can be dragged away to the right — pages need nothing. The pan
starts ≥ 24 px from the left edge, is decided after 8 px (`|dx| > 1.5·|dy|`, rightward only) and drags the page with
a shadow on its edge; the screen Back returns to shows underneath with a scrim and a slight parallax when the shell
has it mounted (root screen, the chat under its info/profile pages), else the bare background. Releasing past 35 %
of the width or with a flick (> 0.5 px/ms) slides the page out and then **clicks the page's back button**
(`[data-shell-back]`, rendered by `AppBar`), so the navigation is exactly the button's (`OnBack`, `BackFallback`,
`NavigationService`, overlay history); otherwise it springs back. A haptic tick marks the commit point; under
reduced motion it still works, without springs or parallax. A touch tap on that back button plays the same exit
before it runs. It never starts while an overlay is open (dialog, sheet, menu, viewer, selection mode, call
screen), inside inputs or horizontal scrollers, or on wide layouts.

**Opt-out**: anything that owns horizontal drags itself (sliders — `Slider` already does —, the composer, the
recorder, a PiP window, custom carousels) carries the attribute `data-no-swipe-back`; the gesture never starts
inside it. Scroll containers inside pages keep vertical scrolling native (`touch-action: pan-y` on the layers).
Implementation: `MainLayout` marks the top layer `[data-swipe-back]` and the screen underneath `[data-swipe-under]`;
`js/app.js` `attachSwipeBack` (see docs/MOTION.md §2).

### Other shell pieces

* `ConnectionStatusBar` — top strip "Ожидание сети… / Подключение… / Обновление…" (after 0.9 s); it slides in/out
  and the content under it follows instead of jumping (`animateStrip`). Static
  `ConnectionStatusBar.Compute(network, realtime, auth)` + `TextKey(status)` if a header wants the same text.
* `NotificationBanner` — in-app banner for `INotificationCoordinator.InAppNotificationRaised`: plays
  `Notifications.MessageTone` when `InAppSounds`, haptic when `InAppVibrate`, tap opens the chat; it drops in with a
  little spring, follows the finger on a swipe up (hides it) and slides away when it goes.
* `<CallOverlay />` (ui-settings-calls) is rendered once by MainLayout above everything; `DialogHost`,
  `ToastHost` too (AuthLayout also has `DialogHost`/`ToastHost`).
* `UpdateNotice` — warning strip when `ServerInfoDto.MinClientVersion` is newer than `IPlatformInfo.AppVersion`
  (`AppVersionCheck.IsOutdated(app, min)`); the signed-out `ServerStatus` shows the same warning.
* `NotificationPermissionPrompt` — asks once per device for notification permission after the signed-in shell
  appears (native: the system dialog via `ILocalNotificationService.RequestPermissionAsync`; browser: a strip with
  a button, because browsers need a tap). Remembered in `IPreferencesStore` under
  `NotificationPermissionPrompt.PromptedKey`; skipped when `Notifications.Enabled` is off. The settings screen
  only needs to offer a manual "turn on" (and `IPermissionService.OpenAppSettingsAsync` when denied).
* `TabBar` / `IconRail` show `TotalUnread` on Chats (the badge pops in / bumps); the active pill slides between
  areas and the selected icon bounces; the rail's avatar opens Settings. `TabBar Active="section"` shows another
  area as active (the shell passes the root screen's area).
* `BootSplash`, `CrashScreen` (`Compact` inside panes), `NotFoundView`, `RedirectTo`, `AuthLayout`.

---

## 4. Navigation & overlays (`NavigationService`)

```csharp
@inject NavigationService Nav
Nav.NavigateTo("/new-group");                  // push (closes open overlays first)
Nav.NavigateTo("/", replace: true);
await Nav.NavigateToAsync(url, replace);       // awaitable form
await Nav.GoBackAsync("/settings");            // top overlay → history back → else replace with fallback
Nav.OpenChat(chatId);                          // wide: replaces when switching chats
Nav.OpenChat(chatId, seq);                     // "/chat/{id}?seq=123" → ChatPage jumps to that message
Nav.OpenChatInfo(chatId);  Nav.OpenUser(userId);  Nav.OpenUsername("anna");
Nav.GoToSection(ShellSection.Settings);        // tab bar / rail semantics
Nav.HandleDeepLink(uri);                       // skymessage://…, https://server/u|join|chat/…; false if not ours
// State: CurrentUrl, CurrentPath, Section, LastDirection, CanGoBack, BackStack, HasOverlays, Changed (event)
// Helpers: NavigationService.ChatIdOf(path), ChatSubPageOf(path), IsTopLevel(path), SectionOf(path), PathOf(url)
```

**Custom overlay** (media viewer, selection mode, expanded call screen):

```csharp
private OverlayHandle? _overlay;

private async Task OpenViewerAsync()
{
    _viewerOpen = true;
    _overlay = await Nav.OpenOverlayAsync(() =>        // runs when Back / Escape / a navigation closes it
    {
        _overlay = null;
        _viewerOpen = false;                           // close the UI only — do not touch the handle here
        return InvokeAsync(StateHasChanged);
    });
}

private async Task CloseViewerAsync()                  // the overlay closes itself (✕ button)
{
    _viewerOpen = false;
    if (_overlay is { } o) { _overlay = null; await o.CloseAsync(); }   // removes the history entry
}
// Dispose: if (_overlay is { IsOpen: true } o) await o.CloseAsync();
```

`OverlayHandle.CloseAsync()` also closes overlays opened above it. Never call `NavigationManager` directly and
don't use `NavigationLock` while overlays are open.

---

## 5. Services

### `ILocalizer` (singleton)

```csharp
L["common.save"]                         // missing → English → the key itself
L.Format("chatlist.leave.title", title)  // string.Format over the template ({0}, {1}…)
L.Plural("plural.members", count)        // picks key.one / key.few / key.many / key.other ({0} = count)
L.Plural("plural.andMoreFiles", n, name) // extra args are {1}, {2}…
L.Error(ex) / L.Error("chat_not_found")  // "error.<code>" with a generic fallback
L.Language ("ru"/"en"), L.Culture, L.Changed, L.Has(key), L.SetLanguage("auto"|"ru"|"en")
```

Russian plural rules: `one` (1, 21, 31…), `few` (2–4, 22–24…), `many` (0, 5–20, 25–30…); always provide all
three for ru plus `other`. Language follows `AppSettings.Language` ("auto" = OS; Russian when the OS language
is Russian or unknown).

### `FormatService` (singleton)

`Time(dt)` "14:05" · `ChatListTime(dt)` (today → time, this week → weekday, this year → "12 мар", else date) ·
`DateChip(dt)` "Сегодня/Вчера/12 марта" · `DateTime(dt)` "вчера в 14:05" · `DayMonthLong/Short/Year(date)` ·
`MonthYear(dt)` · `Duration(TimeSpan|int seconds)` "1:02:03" · `DurationMs(int?)` · `DurationWords(seconds)`
"1 ч 5 мин" · `FileSize(bytes)` "1,4 МБ" · `Number(n)` · `FormatService.BadgeCount(n)` "99+" ·
`Presence(user|presence)` "в сети / был(а) 5 минут назад…" · `Members(n)` · `Typing(events, withNames)` ·
`FormatService.FirstName(name)` · `JoinNames(list)` "Анна, Олег и Мария".

### `IMessagePreviewFormatter` / `MessagePreviewFormatter` (singleton)

`FormatPreview(message)` one-line previews ("🎤 Голосовое сообщение 0:12", "🖼 Фото", deleted…),
`FormatChatTitle(chat)`, plus `FormatSystem(message)` (system events, "Вы …" variants for own actions) and
`FormatCall(message)` (call log lines) — use these for system/call bubbles. `MessagePreviewFormatter.OneLine(text)`.

### `ThemeService` (scoped) + `ThemeCatalog` (singleton)

```csharp
@inject ThemeService Theme
Theme.IsDark; Theme.AccentHex; Theme.Changed;
Theme.Accents              // = Theme.Catalog.Accents. AccentInfo(Key, NameKey, Hex): violet (default), blue, teal, green, orange, pink, red, graphite
Theme.Wallpapers           // = Theme.Catalog.Wallpapers. WallpaperInfo(Key, NameKey, LightCss, DarkCss, LightBase, DarkBase, HasPattern): aurora, dawn, sea, mint, dusk, plain, none
Theme.Catalog.WallpaperCss(key, dark)   // background shorthand for preview tiles
Theme.Catalog.BubbleOutColor(hex, dark) // outgoing bubble color for previews
ThemeCatalog.NormalizeHex("#6c5ce7")    // "#6C5CE7" or null (custom accent validation)
await Theme.PreviewAsync(appearanceCopy); … await Theme.EndPreviewAsync();   // live preview without saving
```

Saving appearance = `Settings.UpdateAsync(s => s.Appearance.X = …)`; ThemeService re-applies automatically
(CSS variables, `data-theme`, reduced motion, compact list, system bars via `ISystemUiService`). Accent names:
`L[accent.NameKey]`, custom: `theme.accent.custom`; modes: `theme.mode.system|light|dark`.

### `LayoutService` (scoped)

`Mode` (Phone/Tablet/Desktop), `IsPhone`, `IsWide`, `HasInfoColumn`, `IsTouch`, `Width`, `Height`,
`KeyboardInset`, `IsKeyboardOpen`, `Changed`. Breakpoints 768 / 1280 px.

### `DialogService` (scoped)

```csharp
@inject DialogService Dialogs
if (await Dialogs.ConfirmAsync(L["…title"], L["…text"], L["common.delete"], danger: true)) { … }
var r = await Dialogs.ConfirmAsync(new ConfirmOptions { Title = …, Message = …, Danger = true,
                                                        CheckboxLabel = L.Format("chatlist.alsoFor", name) });
// r.Confirmed, r.Checked
await Dialogs.AlertAsync(title, text);
string? name = await Dialogs.PromptAsync(new PromptOptions { Title = …, Label = …, InitialValue = …,
                                         MaxLength = 64, AllowEmpty = false, Validate = v => v.Length < 3 ? "…" : null });
var choice = await Dialogs.ChooseAsync(L["chatlist.mute.title"], new[] {        // action sheet (sheet on phones)
    new ChoiceOption<int>(3600, L["chatlist.mute.hour"]) { Icon = IconNames.Clock },
    new ChoiceOption<int>(-1, L["chatlist.mute.forever"]) { Danger = true } });
if (choice is not null) Use(choice.Value);
object? result = await Dialogs.ShowAsync<MyDialog>(L["title"], new Dictionary<string, object?> { ["Param"] = x },
                                                  new DialogOptions { Size = DialogSize.Medium, Presentation = OverlayPresentation.Sheet, Flush = true });
await Dialogs.CloseAllAsync();
```

Custom dialog content component:

```razor
@* MyDialog.razor *@
<p class="sm-dialog__message">…</p>
<DialogActions>
    <Button Variant="ButtonVariant.Ghost" OnClick="Dialog.Cancel">@L["common.cancel"]</Button>
    <Button OnClick="() => Dialog.Close(_value)">@L["common.save"]</Button>
</DialogActions>
@code {
    [CascadingParameter] public DialogReference Dialog { get; set; } = default!;   // Close(result), Cancel(), SetTitle()
    [Parameter] public string? Param { get; set; }
}
```

Results complete after the dialog's history entry is gone, so you can navigate right after `await`.
`DialogOptions`: `Size` (Small/Medium/Large), `Dismissible`, `ShowClose`, `Presentation` (Dialog/Sheet/Auto),
`Flush` (no body padding), `Class`.

### `ToastService` (scoped)

`Toasts.Success(text)`, `Info`, `Warning`, `Error(ex)` / `Error("code")`,
`Toasts.Show(text, ToastKind.Success, L["common.undo"], async () => …)` (action button), `Dismiss(id)`. Toasts rise in,
the others glide to make room, a sideways swipe throws one away, and they fade out when they go. The chat list's
compose button steps up while a toast covers its corner.

### `ISoundService` (scoped) + `ToneCatalog` (singleton)

```csharp
@inject ISoundService Sounds
Sounds.PlayMessageTone(Settings.Current.Notifications.MessageTone);   // "chime" | "pop" | "glass" | "custom" ("none" = silent)
Sounds.StartRingtone(Settings.Current.Notifications.Ringtone);        // "classic" | "soft" | "bright" | "custom" — loops
Sounds.StartRingback();  Sounds.PlayBusy();  Sounds.PlayCallEnd();  Sounds.Stop();
Sounds.Preview("soft");  Sounds.PreviewPack("bells");  Sounds.PreviewCustom(CustomSoundSlot.Ringtone);   // settings
Sounds.Tones.Packs / MessageTones / Ringtones / CallProgress            // SoundPackInfo / ToneInfo — names: L[x.NameKey]
```

Every sound comes from the pack in `Notifications.SoundPack` ("wood" default, "bells", "piano"; keys and repairs in
Core's `SoundCatalog`): our own synthesis, rendered by `tools/tones` (`dotnet run --project tools/tones`, QA:
`python3 tools/tones/analyze.py`) to `wwwroot/sounds/<pack>/<key>.ogg`, which `js/sounds.js` fetches, decodes once
and plays (loops are exactly one cycle). "custom" plays the user's own file (`CustomSoundService` imports it,
`ICustomSoundStore` keeps it per device) and falls back to the pack's default when it cannot. Callers check the
settings (InAppSounds / Sound / CallsEnabled). Browsers unlock audio on the first user gesture.

### Links: `LinkService` (scoped), `TextLinker` (static)

`await Links.OpenAsync(url, visibleText)` — allow-list http/https/mailto/tel; our own links (skymessage://,
https://our-server/u|join|chat) navigate in-app; asks to confirm when the visible text's host differs (or a
named link hides its target); opens via `IExternalLauncher`. `LinkService.TryParse(text, out uri)`,
`IsAllowed(uri)`, `IsOwnLink(uri)`, `NeedsConfirmation(uri, text)`, `Display(uri)`.
`TextLinker.Split(text)` → `TextRun(Kind: Text|Url|Email|Mention, Text, Target)` for plain text without entities.

### Others

* `ShareHelper` (scoped): `await Share.CopyAsync(text, toastText?)` (native clipboard → JS fallback, toast),
  `await Share.ShareAsync(text, title)` (share sheet, copies when unavailable).
* `HapticFeedback` (singleton): `Selection()`, `Tap()`, `LongPress()`, `Success()`, `Warning()` — only when
  supported and `Notifications.InAppVibrate`. Kit toggles/tabs already use it.
* `AvatarUploader` (singleton): `PickedPhoto? p = await Avatars.PickAsync()` (with `p.PreviewUrl`) →
  `MediaDto m = await Avatars.UploadAsync(p, progress)` (compressed to 1280 px, metadata stripped, Ready) → pass
  `m.Id` to `ISkyApi.SetAvatarAsync` / `SetChatAvatarAsync` / `CreateGroupRequest.AvatarMediaId`; then
  `Auth.UpdateMe(me)` for own profile edits.
* `ChatActions` (scoped, Features/ChatList): `TogglePinAsync`, `ToggleMuteAsync` (asks duration), `MuteOptions`,
  `ToggleArchiveAsync` (with Undo), `MarkReadAsync`, `ClearHistoryAsync`, `LeaveAsync`, `DeleteAsync` (per chat
  type), `DeleteGroupForEveryoneAsync`, `TitleOf(chat)` — reuse them in the chat header menu. The chat list's swipe
  buttons run them too (left: Mute / Archive, a full swipe archives; right: Pin / Read, a full swipe toggles pin).
* `ChatRules` (static, mirrors server policy): `chat.IsAdmin()`, `CanSendMessages()`, `CanSendMedia()`,
  `CanAddMembers()`, `CanPinMessages()`, `CanChangeInfo()`, `CanChangePermissions()`,
  `CanDeleteForEveryone(me, senderId)`, `CanDeleteChatForEveryone()`, `IsMuted(now)`, `IsMutedForever()`,
  `LastMessageReadByOthers()`, `ChatRules.CanRemoveMember(actor, target)`, `CanSetRoles(actor)`.
* `PeopleExtensions`: `Users.Resolve(id, embedded)`, `user.NameOf(L)`, `Users.NameOf(id, L, embedded)`,
  `user.IsOnline()`, `user.Handle()` ("@name").
* `AvatarPalette`: `Background(id)` (gradient), `Color(id)` (sender name color), `Initials(name)`.
* `SettingsRoutes`: section keys `profile, phone, privacy, blocked, sessions, notifications, appearance, chats,
  devices, data, language, connection, about`; `SettingsRoutes.PathOf(SettingsRoutes.Privacy)`. Other screens
  link to these (profile "Edit" → `/settings/profile`) — the settings feature must render them.
* `UiDispatcher` (scoped): `Post(work)` / `InvokeAsync(work)` on the renderer from non-component services.
* `AnonymousPageAttribute`: `@attribute [AnonymousPage]` for signed-out pages.
* `EmojiCatalog` (singleton): `Categories` (Key, NameKey, Icon, Emoji), `All`, `QuickReactions`
  (👍 ❤️ 😂 😮 😢 🙏 🔥 👏 🎉 😍), `DefaultRecent`; `EmojiRecents`: `GetAsync()`, `AddAsync(e)`, `Changed`.
* `RecentSearches` (singleton, Features/Search): `GetAsync/AddAsync/RemoveAsync/ClearAsync`.

---

## 6. Kit components

All take `Class` for extra classes. Sizes are px at 100% font scale (rendered in rem, so they follow the font
size setting).

### Base class

```razor
@inherits UiComponentBase           @* gives L (ILocalizer) — don't @inject ILocalizer L again *@
@code {
    protected override void OnInitialized()
    {
        RenderOn(h => ChatList.Changed += h, h => ChatList.Changed -= h);                       // Action events
        RenderOn<Guid>(h => Users.UserChanged += h, h => Users.UserChanged -= h, id => id == UserId); // filtered
        OnDispose(() => _cts.Cancel());                                                          // any cleanup
    }
    protected override void Dispose(bool disposing) { /* extra cleanup */ }
    // RequestRender(): coalesced (30 ms) thread-safe re-render; RenderNowAsync(); IsDisposed
}
```

If you also implement `IAsyncDisposable`, call `Dispose()` at the end of `DisposeAsync` (Blazor calls only
`DisposeAsync`).

### Icon, Logo

```razor
<Icon Name="@IconNames.Search" />                 @* 20 px *@
<Icon Name="@IconNames.Mic" Size="24" StrokeWidth="2.4" Title="Микрофон" Spin="false" />
<Logo Size="64" />   <Logo Size="40" Mark="true" />   @* tile / cloud only *@
```

Icons are Lucide (ISC, `lucide-static` 1.57.0, license in `wwwroot/licenses/lucide.txt`), embedded as
`IconNames.*` constants. Available: ALargeSmall Apple Archive ArchiveRestore ArrowDown ArrowDownToLine ArrowLeft
ArrowRight ArrowUp ArrowUpRight AtSign AudioLines AudioWaveform BadgeAlert BadgeCheck BadgeInfo Ban Battery
BatteryCharging Bell BellOff BellRing Bluetooth Bold BookOpen BookUser Bookmark Calendar Camera CameraOff Car
Check CheckCheck ChevronDown ChevronLeft ChevronRight ChevronUp ChevronsDown Circle CircleAlert CircleCheck
CircleCheckBig CircleDot CircleEllipsis CircleHelp CircleMinus CirclePause CirclePlay CirclePlus CircleStop
CircleUser CircleUserRound CircleX Clock Cloud CloudAlert CloudDownload CloudOff Code Coffee Contact ContactRound
Copy CornerDownLeft CornerUpLeft CornerUpRight Crown Database DoorOpen Download Ear Earth Ellipsis
EllipsisVertical Eraser Expand ExternalLink Eye EyeOff FastForward File FileArchive FileAudio FileDown FileImage
FileText FileVideo Film Flag FlipHorizontal2 FolderOpen Forward Gauge Ghost Globe GripHorizontal GripVertical
Hand HardDrive Hash Headphones Heart History Hourglass House Image ImagePlus ImageUp Images Inbox Info Italic
KeyRound Keyboard Languages Laptop LayoutGrid Leaf LifeBuoy Lightbulb Link Link2 Link2Off List ListChecks
ListFilter ListOrdered ListX LoaderCircle Lock LockOpen LogIn LogOut Mail Maximize2 Menu MessageCircle
MessageCircleMore MessageCircleOff MessageCirclePlus MessageSquare MessageSquarePlus MessagesSquare Mic MicOff
Minimize2 Minus Monitor MonitorOff MonitorSmartphone MonitorUp Moon Music Network OctagonAlert Paintbrush Palette
PanelLeft PanelRight Paperclip Pause PawPrint Pencil Phone PhoneCall PhoneForwarded PhoneIncoming PhoneMissed
PhoneOff PhoneOutgoing PictureInPicture2 Pin PinOff Plane Play PlugZap Plus QrCode Quote RefreshCcw RefreshCw
Reply Rewind RotateCcw RotateCw Save ScanQrCode ScreenShare ScreenShareOff ScrollText Search SearchX Send
SendHorizontal Server ServerOff Settings Settings2 Share Share2 Shield ShieldAlert ShieldBan ShieldCheck Shrink
Signal SignalHigh SignalLow SignalMedium SlidersHorizontal Smartphone Smile SmilePlus Sparkles Speaker Square
SquareCheck SquarePen Star Strikethrough Sun SunMoon SwatchBook SwitchCamera TabletSmartphone ThumbsUp Ticket
Timer Trash Trash2 TriangleAlert Trophy Type Underline Undo2 Unplug Upload User UserCheck UserCog UserMinus
UserPen UserPlus UserRound UserRoundPlus UserRoundX UserSearch UserX Users UsersRound Vibrate Video VideoOff
Volume Volume1 Volume2 VolumeX Wifi WifiOff X Zap ZoomIn ZoomOut. Need another one? Ask ui-kit/the lead (the
data file is generated); don't inline other SVG icon sets.

### Avatars

```razor
<UserAvatar UserId="id" Embedded="dto" Size="44" ShowOnline="true" OnClick="…" />   @* live from IUserStore *@
<ChatAvatar Chat="summary" Size="54" />          @* Saved bookmark / private peer (online dot) / group photo *@
<Avatar Url="@MediaUrls.AvatarUrl(mediaId)" Name="Анна" Seed="id" Size="40" Online="false" TrackOnline="true"
        Icon="@IconNames.Ghost" Background="var(--sm-accent)" Square="false" />   @* TrackOnline: the dot pops in/out *@
<UserName UserId="id" Embedded="dto" Colored="true" FirstNameOnly="true" />          @* sender names *@
<PresenceText UserId="id" Embedded="dto" />      @* "в сети" (accent) / "был(а) …", ticks every minute *@
```

### Buttons

```razor
<Button OnClick="SaveAsync" Loading="_saving" Icon="@IconNames.Check">@L["common.save"]</Button>
<Button Variant="ButtonVariant.Secondary|Tonal|Ghost|Danger|DangerGhost|Link" Size="ButtonSize.Small|Large"
        Block="true" Type="submit" Disabled="…" IconEnd="@IconNames.ArrowRight" Haptic="true">…</Button>
<IconButton Icon="@IconNames.Phone" Label="@L["calls.call"]" OnClick="…" />           @* Label = tooltip + aria *@
<IconButton Icon="…" Label="…" Variant="IconButtonVariant.Tonal|Filled|Danger|OnMedia" Size="48"
            Active="_muted" Toggle="true" Badge="3" Dot="true" StopPropagation="true" />
```

### Form fields

```razor
<TextField @bind-Value="_name" Label="…" Placeholder="…" Hint="…" Error="@_error" Success="@_ok"
           MaxLength="64" ShowCounter="true" Icon="@IconNames.AtSign" Prefix="@("@")" Type="text|password|email|tel|url|number"
           Clearable="true" Autofocus="true" Autocomplete="username" InputMode="numeric" EnterKeyHint="send"
           Immediate="true" OnEnter="SubmitAsync" OnBlurred="…">
    <Trailing><Spinner Size="18" /></Trailing>
</TextField>
@code { TextField? _field; await _field!.FocusAsync(selectText: true); }
<TextArea @bind-Value="_bio" Label="…" Rows="3" MaxRows="8" MaxLength="200" ShowCounter="true" OnKeyDownEvent="OnKey" />
@* TextArea.Element (ElementReference), FocusAsync(); auto-grows; re-measures when Value changes from outside *@
<Switch @bind-Checked="_on" Label="…" Description="…" />
<Checkbox @bind-Checked="_c" Label="…" Round="true" />
<Radio TValue="string" Value="ru" @bind-Selected="_lang" Name="lang" Label="Русский" />
<Slider @bind-Value="_font" Min="85" Max="140" Step="5" Label="…" ShowValue="true" Format="v => $"{v}%"" OnChangeEnd="SaveAsync" />
<Select TValue="VideoQuality" Options="_qualityOptions" @bind-Value="_quality" Label="…" />
<SearchBox @bind-Value="_q" OnSearch="SearchAsync" DebounceMs="300" Autofocus="true" ShowCancel="true" OnCancel="…" Busy="_loading" />
```

### Lists & sections

```razor
<Section Title="@L["settings.notifications.title"]" Footer="explanation under the card" DividerInset="64px">
    <ListItem Icon="@IconNames.Bell" IconColor="#2F7FEC" Title="…" Subtitle="…" Meta="Вкл." Chevron="true"
              OnClick="…" OnContextMenu="pos => _menu!.OpenAsync(pos)" />
    <ListItem Title="…" Danger="true" Selected="true" Compact="true" Multiline="true" Href="/settings/about" />
    <ListItem>
        <Leading><UserAvatar UserId="id" Size="44" /></Leading>
        <ChildContent>      @* custom body; use .sm-list-item__title / .sm-list-item__subtitle *@
            <div class="sm-list-item__title">…</div><div class="sm-list-item__subtitle">…</div>
        </ChildContent>
        <Trailing><Badge Count="3" /></Trailing>
    </ListItem>
    <SwitchItem Icon="@IconNames.Vibrate" IconColor="#2E9E5B" Title="…" Subtitle="…" @bind-Checked="_vibrate" />
</Section>
<Section Flat="true"> … </Section>                     @* no card *@
<ChoiceList TValue="ThemeMode" Options="_modes" @bind-Value="_mode" />   @* radio rows (theme, language, privacy level) *@
```

`DividerInset`: 16px (plain), 64px (with icon tiles), 74–78px (with 44–48 px avatars). Icon tile colors: use the
accent palette hexes (`ThemeCatalog.Accents`) or `var(--sm-accent)`.

### Status & placeholders

```razor
<Badge Count="5" Muted="true" Max="99" />  <Badge Kind="BadgeKind.Mention" />  <Badge Kind="BadgeKind.Dot" />
<Badge Count="n" Animate="true" />   @* keep it rendered (nothing at 0): pops in when a count appears, bumps on change *@
<Badge Kind="BadgeKind.Mention" Pop="true" />   @* pops in when rendered (a mention that just arrived) *@
<Badge Kind="BadgeKind.Label" Text="админ" />  <Badge Kind="BadgeKind.Outline" Text="…" />
<Spinner Size="24" Inherit="true" />
<Skeleton Shape="SkeletonShape.Line|Circle|Rect" Width="60%" Height="12px" />   <SkeletonList Rows="8" Avatar="true" TwoLines="true" />
<EmptyState Icon="@IconNames.Phone" Title="…" Text="…" Fill="true">
    <Actions><Button>…</Button></Actions>
</EmptyState>
<ProgressRing Value="0.42" Size="44" OnCancel="CancelUpload" />   <ProgressRing Indeterminate="true" />
<Chip Text="Анна" OnRemove="…"><Leading><UserAvatar UserId="id" Size="24" /></Leading></Chip>
```

### Overlays

```razor
<Dialog @bind-Open="_open" Title="…" Size="DialogSize.Medium" Presentation="OverlayPresentation.Auto" Dismissible="true" Flush="false" OnClosed="…">
    <ChildContent>…</ChildContent>
    <Footer><Button OnClick="() => _open = false">@L["common.done"]</Button></Footer>
</Dialog>
<BottomSheet @bind-Open="_sheet" Title="…">…</BottomSheet>       @* sheet on phones (handle, swipe down), card on wide *@
<DialogActions Stack="true">…buttons…</DialogActions>           @* right-aligned button row inside dialog content *@

<ContextMenuArea OnContextMenu="pos => _menu!.OpenAsync(pos)" AllowTextSelection="false">  … bubble …  </ContextMenuArea>
<ContextMenu @ref="_menu" Title="…" PreferSheet="true" OnClosed="…">
    <Header>@* e.g. quick reactions row *@</Header>
    <MenuItem Icon="@IconNames.Reply" Label="@L["chat.reply"]" OnClick="ReplyAsync" />
    <MenuDivider />
    <MenuItem Icon="@IconNames.Trash2" Label="…" Danger="true" Hint="…" KeepOpen="false" OnClick="…" />
</ContextMenu>
@code { ContextMenu? _menu; /* await _menu.OpenAsync(x, y) / OpenAsync(MenuPosition); CloseAsync(); IsOpen */ }
```

Right-click and touch long-press (450 ms, movement-tolerant, swallows the trailing click, haptic) come from
`ContextMenuArea` or `ListItem.OnContextMenu`. Menus are popovers on wide screens and bottom sheets on phones
(`PreferSheet="false"` keeps the popover). Dialogs, sheets and menus play a short exit when they close (the markup
stays ~230 ms, inert to taps: `.sm-overlay--leaving` / `.sm-menu-layer--leaving`); sheets arrive with a slight
spring and leave from wherever a drag left them.

### Tabs, segmented control

```razor
<Tabs TValue="SharedMediaFilter" Items="_tabs" @bind-Active="_filter" Variant="TabsVariant.Underline|Pill" />
<SegmentedControl TValue="double" Options="_speeds" @bind-Value="_speed" Label="…" />
@code {
    IReadOnlyList<ChoiceOption<SharedMediaFilter>> _tabs = [new(SharedMediaFilter.Media, L["media.tab.media"]) { Icon = IconNames.Image, Badge = 3 }];
}
```

### QR, color swatches, emoji, text

```razor
<QrCode Text="@link" Size="220" />                         @* QRCoder PNG data URL, rendered locally *@
<div class="sm-swatches">
    @foreach (var a in Theme.Accents) { <ColorSwatch Color="@a.Hex" Label="@L[a.NameKey]" Selected="@(a.Key == key)" OnClick="…" /> }
    <ColorSwatch Custom="true" Label="@L["theme.accent.custom"]" OnClick="PickCustom" />
</div>
<EmojiPicker OnPick="InsertEmoji" Height="320px" ShowRecent="true" />   @* recent emoji are saved per account *@
<LinkifiedText Text="@bio" />                              @* plain text + safe auto-links + @mentions *@
```

---

## 7. CSS: tokens and utilities (`wwwroot/css/app.css`)

Theme: `html[data-theme="light"|"dark"]`, plus `data-theme-mode`, `data-reduce-motion`, `data-compact`,
`data-input="keyboard|pointer"` on `<html>`. Accent/wallpaper/font/bubble tokens are generated by ThemeService.

**Tokens** — color: `--sm-accent --sm-accent-hover --sm-accent-soft --sm-on-accent --sm-link --sm-selection
--sm-bg --sm-surface --sm-surface-2 --sm-surface-3 --sm-text --sm-text-2 --sm-text-3 --sm-border --sm-divider
--sm-danger(-soft) --sm-success(-soft) --sm-warning(-soft) --sm-online --sm-scrim --sm-hover --sm-press`;
bubbles: `--sm-bubble-out --sm-bubble-out-text --sm-bubble-out-meta --sm-bubble-in --sm-bubble-in-text
--sm-bubble-in-meta --sm-bubble-radius`; chat background: `--sm-chat-bg` (base color) + `--sm-chat-wallpaper`
(full `background` shorthand: `background: var(--sm-chat-wallpaper); background-color: var(--sm-chat-bg);`);
shape/motion: `--sm-radius-s/m/l/xl --sm-shadow-1 --sm-shadow-2 --sm-ease --sm-ease-out --sm-dur --sm-dur-fast`;
type/layout: `--sm-font --sm-font-mono --sm-font-size --sm-header-h --sm-tabbar-h --sm-rail-w --sm-sidebar-w
--sm-info-w`; insets: `--sm-safe-top/bottom/left/right` and `--sm-kb-inset` (set by the MAUI host; **use the
derived** `--sm-inset-top/bottom/left/right` which fall back to `env(safe-area-inset-*)`), `--sm-keyboard`
(native or visual-viewport keyboard height), `--sm-bottom-space` (inside the shell: nav-bar inset, 0 while the
keyboard is open — use for composers/footers), `--sm-overlay-bottom` (fixed overlays), `--sm-page-bottom`
(set to 0 above the tab bar). The shell already applies top/left/right insets and ends above the keyboard.

**Utilities** — layout: `.sm-row .sm-row--top/--wrap/--between/--end .sm-col .sm-grow .sm-shrink-0 .sm-center
.sm-fill .sm-relative .sm-gap-0/xs/s/m/l/xl .sm-p-s/m/l .sm-px-m .sm-py-s/m .sm-mt-s/m/l .sm-mb-s/m .sm-mr-s
.sm-ml-auto .sm-w-full .sm-hidden .sm-sr-only .sm-scroll .sm-page-content(--narrow) .sm-split .sm-card
.sm-divider .sm-safe-top .sm-safe-bottom .sm-kb-pad .sm-pressable .sm-no-select`; text: `.sm-title-xl .sm-title-l
.sm-title .sm-subtitle .sm-body .sm-caption .sm-small .sm-mono .sm-muted .sm-faint .sm-accent-text
.sm-danger-text .sm-success-text .sm-strong .sm-center-text .sm-truncate .sm-clamp-2/3 .sm-pre-wrap .sm-break
.sm-nums .sm-emoji .sm-link .sm-mention`; kit classes usable on raw elements: `.sm-btn(--primary|--secondary|
--tonal|--ghost|--danger|--danger-ghost|--link|--s|--l|--block) .sm-icon-btn(--tonal|--filled|--danger|--on-media)
.sm-list-item(__title|__subtitle) .sm-section .sm-badge .sm-chip .sm-menu-item .sm-dialog__message
.sm-dialog-actions .sm-skeleton .sm-swatches`. Animations: `sm-fade-in sm-pop-in sm-sheet-in sm-slide-in-right
sm-slide-in-left sm-drop-in sm-rise-in sm-spin sm-shimmer sm-pulse` plus the docs/MOTION.md set (`sm-pop sm-bump
sm-msg-in sm-msg-out sm-shake`), exits that name only their end state so they continue from wherever the element is
(`sm-fade-out sm-pop-out sm-sheet-out sm-toast-out`), `sm-sheet-spring` and `sm-skeleton-sweep` — all disabled by
reduced motion (`prefers-reduced-motion` or the Animations setting) except spinners. `@property --row-x/--row-w`
are registered for swipeable list rows (below).

**Scoped CSS**: put component styles in `Component.razor.css`. Scoping applies only to elements written in that
`.razor` file — style child components' elements with `.my-wrapper ::deep .their-class` (a scoped ancestor is
required; wrap in a div if the child component is your root). Use `html[data-theme="dark"] .x { … }` for
theme-specific tweaks (no `:global`). Never put `transform`, `filter`, `backdrop-filter`, `contain` or
`will-change: transform` on ancestors of overlays (it breaks `position: fixed`).

---

## 8. JS helpers (`AppJs`, scoped wrapper over `js/app.js`)

```csharp
@inject AppJs Js
await Js.FocusAsync(el, selectText);  await Js.FocusFirstAsync(container);  await Js.BlurActiveAsync();
bool ok = await Js.CopyTextAsync(text);
JsHandle h = await Js.ObserveVisibilityAsync(el, dotnetRef, nameof(OnVisible), "0px", 0.5);   // OnVisible(bool visible, double ratio)
JsHandle io = await Js.CreateIntersectionObserverAsync(scrollEl, dotnetRef, nameof(OnBatch), "0px", 0.6);
await io.InvokeVoidAsync("observe", bubbleEl, messageId.ToString()); await io.InvokeVoidAsync("unobserve", bubbleEl);
                                                        // OnBatch(VisibilityChange[] changes) — Key, Visible, Ratio
await Js.AttachContextMenuAsync(el, ref, nameof(OnMenu));  await Js.AttachLongPressAsync(el, ref, nameof(OnLong), 450); // (double x, double y)
await Js.OnClickOutsideAsync(el, ref, nameof(OnOutside));
await Js.AttachSwipeAsync(el, ref, nameof(OnSwiped), "down|up|left|right", 80);   // element follows the finger
await Js.AttachSwipeAsync(el, ref, nameof(OnSwiped), "x", 64, fling: true, arg: id);  // either side, flies out, OnSwiped(id)
await Js.AttachSwipeBackAsync(phoneContainer, ref);   // MainLayout only: swipe back (OnSwipeBackCommit, OnSwipeBackThreshold)
await Js.ObserveViewportAsync(ref, nameof(OnViewport));  ViewportInfo? v = await Js.GetViewportAsync();
await Js.ScrollToTopAsync(el); await Js.ScrollToBottomAsync(el, smooth); await Js.ScrollIntoViewAsync(el, "center");
ScrollInfo? s = await Js.GetScrollAsync(el);  /* Top, Height, ClientHeight, FromBottom */  ElementRect? r = await Js.GetRectAsync(el);
await Js.PositionPopoverAsync(el, x, y);  JsHandle g = await Js.AutoGrowAsync(textarea, 6);  /* g.InvokeVoidAsync("refresh") */
JsHandle t = await Js.TrapFocusAsync(container);
await Js.LocalGetAsync(key) / LocalSetAsync(key, value);   // per-device UI prefs only (never account data)
await Js.SetDocumentTitleAsync(title);
await Js.InvokeAsync<T>("fn", args) / InvokeVoidAsync / AttachAsync   // other exports of app.js
```

Other gesture/motion exports (through `Js.AttachAsync("name", …)`): `attachRowSwipe(list, scroller, ref)` —
swipeable rows `[data-swipe-row]` with buttons in `[data-swipe-actions="start|end"]`, positioned from `--row-x/--row-w`
(the chat list; .NET gets `OnRowSwipeArm(key)`, `OnRowAction(key, action)`, `OnRowSwipeThreshold()`);
`attachFlipList(container, keyAttr, { enter, viewport })` — keyed children glide when reordered (FLIP, transform
only; not on first render, while covered, under reduced motion or for big changes; `suppress()` before swapping the
whole list); `attachScrollHide(scroller, target, attr)` — sets `attr` while scrolling down (floating buttons);
`animateStrip(el, show)` — a strip at the top of the shell slides in/out and the content follows.

Every `JsHandle` must be disposed (`await h.DisposeAsync()`), every `DotNetObjectReference` too. All calls
swallow "page gone" errors. Your own feature JS (`chat.js`, `recorder.js`, `calls.js`, `devices.js`…) is a
separate ES module you import yourself (`IJSRuntime.InvokeAsync<IJSObjectReference>("import",
"./_content/SkyMessage.Client.UI/js/chat.js")`).

`boot.js` exposes `window.__smBoot` (theme) and `window.__smHistory` (overlay history) — use them only through
`NavigationService`/`ThemeService`. `sounds.js` is used only through `ISoundService`.

---

## 9. Stubs to replace (keep these contracts)

| File | Owner | Keep |
|---|---|---|
| `Features/Chat/ChatPage.razor` | ui-chat | `@page "/chat/{ChatId:guid}"`, `[Parameter] Guid ChatId`, `[SupplyParameterFromQuery(Name = "seq")] long? Seq` (jump target from search: `IMessageStore.JumpToAsync` instead of `OpenAsync`), `[CascadingParameter] ShellContext? Shell` + the IsCovered rule, set/clear `ActiveChatId`. Rendered by MainLayout on wide screens. Info button → `Nav.OpenChatInfo(ChatId)` (the info page is ui-kit's `GroupInfoPage` for all chat types); shared media → `/chat/{id}/media`. Place `<GroupCallBanner ChatId="ChatId"/>` under the header. Header back: `Back="@(Shell?.IsPhone ?? true)"`. |
| `Features/Calls/CallOverlay.razor` | ui-settings-calls | No parameters; rendered once by MainLayout above everything. |
| `Features/Calls/GroupCallBanner.razor` | ui-settings-calls | `[Parameter] Guid ChatId`. |
| `Features/Calls/CallsPage.razor` | ui-settings-calls | `@page "/calls"`; full width on wide screens. |
| `Features/Settings/SettingsPage.razor` | ui-settings-calls | `@page "/settings"`, `@page "/settings/{Section}"`, `[Parameter] string? Section` with the `SettingsRoutes` keys; keep a sign-out entry; full width on wide screens (`.sm-split`). The parameter name does not clash with the kit's `<Section>` tag in markup; in C# code refer to the component type as `Kit.Section` if you need it. |

ui-kit pages you can link to: `/` (chat list), `/contacts`, `/contacts/import`, `/user/{id}`, `/u/{username}`,
`/new-group`, `/chat/{id}/info`, `/join/{token}`, `/search` (`?q=` prefill), `/welcome`, `/login`, `/register`.
Reusable feature components: `UserPicker` / `UserPickerDialog` (Features/Contacts: single or multiple people,
`Exclude`, result `UserSummaryDto` or `IReadOnlyList<UserSummaryDto>` — e.g. "Forward to…" can use the chat list
instead), `ProfileView` (Features/Profile), `QrShareDialog`, `AvatarViewerDialog`, `InviteFailuresDialog`,
`ChatListItem`, `ChatActions`, `AddContactDialog`, `ServerPickerDialog` + `ServerStatus` (connection settings may
reuse them; `ServerPickerDialog.Normalize(url)`).

---

## 10. Checking screens without a server

The API must not be started by feature agents, but screens can still be rendered headlessly: a throwaway console
project **outside the repo** (your scratchpad) with `bunit` 2.11.3 (MIT) + `NSubstitute` 6.2.0, referencing the
DLLs built into your artifacts folder (`artifacts/<agent>/bin/SkyMessage.Client.UI/debug/*.dll`, with
`ManagePackageVersionsCentrally=false`):

```csharp
await using var ctx = new BunitContext();
ctx.JSInterop.Mode = JSRuntimeMode.Loose;                       // every app.js call returns a default
ctx.Services.AddSingleton(Substitute.For<IChatListStore>());     // …one substitute per Core/platform interface
settings.Language = "ru";                                         // ISettingsService.Current (else the OS language wins)
ctx.Services.AddSingleton(new ClientCoreOptions());
ctx.Services.AddSkyMessageUI();
var cut = ctx.Render<Routes>();                                   // the real router, auth gate and shell
ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/chat/…");
cut.WaitForAssertion(() => { if (!cut.Markup.Contains("…")) throw new Exception("not yet"); });  // no "class=\"crash" either
```

`IPlatformInfo.IsMobile = true` gives the phone shell (tab bar, page layers), `false` the wide one. ui-kit's own
routes, dialogs and both layouts pass this check; it does not cover JS behavior (history, gestures, observers).
