using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Services;

/// <summary>Top-level areas of the app (bottom tab bar on phones, icon rail on wide screens).</summary>
public enum ShellSection
{
    Chats,
    Contacts,
    Calls,
    Settings,
}

public enum NavDirection
{
    None,
    Forward,
    Back,
}

/// <summary>
/// An open overlay's history entry (dialog, sheet, menu, viewer, selection mode...). Dispose or
/// <see cref="CloseAsync"/> it when the overlay closes by itself; when the user presses Back the service
/// calls the overlay's dismiss callback instead and the handle becomes closed.
/// </summary>
public sealed class OverlayHandle : IAsyncDisposable
{
    private readonly NavigationService _owner;
    private int _closed;

    internal OverlayHandle(NavigationService owner, string id)
    {
        _owner = owner;
        Id = id;
    }

    public string Id { get; }

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    internal bool MarkClosed() => Interlocked.Exchange(ref _closed, 1) == 0;

    /// <summary>Removes the history entry (and any overlay opened above this one). Idempotent.</summary>
    public Task CloseAsync() => IsOpen ? _owner.CloseOverlayAsync(this) : Task.CompletedTask;

    public async ValueTask DisposeAsync() => await CloseAsync();
}

/// <summary>
/// The only place that navigates, parses deep links and manages overlay history entries. Every overlay
/// pushes a same-URL history entry, so Android Back / browser Back / Escape close overlays first. Keeps an
/// in-app back stack (for in-app back buttons and slide directions) and the pending deep link of a signed-out
/// user, which opens after sign-in. Scoped.
/// </summary>
public sealed class NavigationService : IAsyncDisposable
{
    public const string WelcomePath = "/welcome";
    private const int MaxStack = 64;
    private static readonly TimeSpan PendingWindow = TimeSpan.FromSeconds(2);

    private readonly NavigationManager _nav;
    private readonly AppJs _js;
    private readonly IDeepLinkSource _deepLinks;
    private readonly IAuthService _auth;
    private readonly LayoutService _layout;
    private readonly UiDispatcher _dispatcher;
    private readonly ILogger<NavigationService> _logger;
    private readonly List<string> _stack = [];
    private readonly List<OverlayEntry> _overlays = [];
    private DotNetObjectReference<NavigationService>? _self;
    private PendingKind _pendingKind;
    private DateTimeOffset _pendingAt;
    // The last page pushed by NavigateToAsync until its LocationChanged arrives (CurrentUrl is still the old page).
    private string? _requested;
    private DateTimeOffset _requestedAt;
    private string? _pendingDeepLink;
    private bool _initialized;
    private bool _disposed;

    public NavigationService(NavigationManager nav, AppJs js, IDeepLinkSource deepLinks, IAuthService auth, LayoutService layout,
        UiDispatcher dispatcher, ILogger<NavigationService> logger)
    {
        _nav = nav;
        _js = js;
        _deepLinks = deepLinks;
        _auth = auth;
        _layout = layout;
        _dispatcher = dispatcher;
        _logger = logger;
        try
        {
            CurrentUrl = Relative(nav.Uri);
        }
        catch (InvalidOperationException)
        {
            // NavigationManager not initialized yet; InitializeAsync reads it again.
            CurrentUrl = "/";
        }
        Section = SectionOf(PathOf(CurrentUrl)) ?? ShellSection.Chats;
    }

    private enum PendingKind
    {
        None,
        Push,
        Replace,
        Back,
        /// <summary>The replace that ends <see cref="StartOverAsync"/>: the in-app history starts over with it.</summary>
        Reset,
    }

    private sealed record OverlayEntry(string Id, Func<Task> OnDismiss, OverlayHandle Handle);

    /// <summary>Current app-relative URL including the query, e.g. "/chat/…?seq=5".</summary>
    public string CurrentUrl { get; private set; }

    /// <summary>Current path without query, always starting with '/'.</summary>
    public string CurrentPath => PathOf(CurrentUrl);

    public ShellSection Section { get; private set; }

    /// <summary>Direction of the last navigation (drives slide transitions on phones).</summary>
    public NavDirection LastDirection { get; private set; }

    /// <summary>There is an in-app page to go back to.</summary>
    public bool CanGoBack => _stack.Count > 1;

    /// <summary>In-app history, oldest first; the last entry is the current URL.</summary>
    public IReadOnlyList<string> BackStack => _stack;

    /// <summary>The chat a deeper page was opened from (profile from a chat header…), so the shell can keep it mounted.</summary>
    public Guid? ChatBelowCurrent()
    {
        for (var i = _stack.Count - 2; i >= 0; i--)
        {
            var path = PathOf(_stack[i]);
            if (ChatIdOf(path) is { } id) return id;
            if (IsTopLevel(path)) return null;
        }
        return null;
    }

    public bool HasOverlays => _overlays.Count > 0;

    /// <summary>Deep link waiting for sign-in ("/u/alice", "/join/…").</summary>
    public string? PendingDeepLink => _pendingDeepLink;

    /// <summary>Location, section or overlay stack changed. Fires on the renderer's dispatcher.</summary>
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        CurrentUrl = Relative(_nav.Uri);
        _stack.Clear();
        _stack.Add(CurrentUrl);
        Section = SectionOf(CurrentPath) ?? ShellSection.Chats;
        _nav.LocationChanged += OnLocationChanged;
        _deepLinks.DeepLinkReceived += OnDeepLinkReceived;
        _auth.Changed += OnAuthChanged;
        _self = DotNetObjectReference.Create(this);
        await _js.HistoryAttachAsync(_self);
        if (_deepLinks.ConsumePending() is { } pending) HandleDeepLink(pending);
    }

    // ---- Navigation ----

    /// <summary>Navigates in-app. Open overlays are closed first (their history entries removed).</summary>
    public void NavigateTo(string url, bool replace = false) => _ = NavigateToAsync(url, replace);

    public async Task NavigateToAsync(string url, bool replace = false)
    {
        if (_disposed) return;
        try
        {
            var target = Normalize(url);
            // A second tap on the same row before the first push arrived: pushing again would leave a duplicate entry
            // that the next Back only steps over.
            if (!replace && target == _requested && DateTimeOffset.UtcNow - _requestedAt <= PendingWindow) return;
            if (!replace)
            {
                _requested = target;
                _requestedAt = DateTimeOffset.UtcNow;
            }
            if (_overlays.Count > 0) await CloseAllOverlaysAsync();
            if (target == CurrentUrl && !replace)
            {
                _requested = null;
                return;
            }
            SetPending(replace ? PendingKind.Replace : PendingKind.Push);
            _nav.NavigateTo(target, new NavigationOptions { ReplaceHistoryEntry = replace });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Navigation failed");
        }
    }

    /// <summary>
    /// In-app back: closes the top overlay, else goes back in history when there is an in-app page behind,
    /// else replaces the current page with <paramref name="fallback"/>.
    /// </summary>
    public async Task GoBackAsync(string? fallback = "/")
    {
        if (_disposed) return;
        if (_overlays.Count > 0)
        {
            var top = _overlays[^1];
            _overlays.RemoveAt(_overlays.Count - 1);
            if (top.Handle.MarkClosed()) await DismissSafeAsync(top);
            await _js.HistoryCloseAsync(top.Id);
            RaiseChanged();
            return;
        }
        if (_stack.Count > 1)
        {
            SetPending(PendingKind.Back);
            await _js.HistoryBackAsync();
            return;
        }
        var target = Normalize(fallback ?? "/");
        if (target == CurrentUrl) return;
        SetPending(PendingKind.Replace);
        _nav.NavigateTo(target, new NavigationOptions { ReplaceHistoryEntry = true });
    }

    /// <summary>
    /// Opens a chat. With <paramref name="seq"/> the chat jumps to that message ("/chat/{id}?seq=").
    /// On wide screens switching from one chat to another replaces the history entry.
    /// </summary>
    public void OpenChat(Guid chatId, long? seq = null)
    {
        var url = seq is { } s ? $"{DeepLinks.ChatPath(chatId)}?seq={s}" : DeepLinks.ChatPath(chatId);
        var replace = _layout.IsWide && ChatIdOf(CurrentPath) is not null;
        NavigateTo(url, replace);
    }

    /// <summary>
    /// Leaves the pages of a chat that just went away (left, deleted): goes back past every entry of that chat on top
    /// of the history, so Back cannot lead into it again ("Chat not found"). When nothing older is left, the current
    /// page is replaced with the chat list. Does nothing when the current page is not one of that chat's.
    /// </summary>
    public async Task LeaveChatAsync(Guid chatId)
    {
        if (_disposed) return;
        var depth = 0;
        while (depth < _stack.Count && ChatIdOf(PathOf(_stack[^(depth + 1)])) == chatId) depth++;
        if (depth == 0) return;
        try
        {
            if (_overlays.Count > 0) await CloseAllOverlaysAsync();
            if (depth < _stack.Count)
            {
                SetPending(PendingKind.Back);
                await _js.HistoryGoAsync(-depth);
                return;
            }
            SetPending(PendingKind.Replace);
            _nav.NavigateTo("/", new NavigationOptions { ReplaceHistoryEntry = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Leaving the chat's pages failed");
        }
    }

    /// <summary>
    /// The auth gate's redirect right after a sign-in or sign-out: goes back to the first in-app entry (unseen by the
    /// router) and replaces it with <paramref name="url"/>. Nothing of the other session stays behind it, so Back
    /// leaves the app instead of walking through pages that only redirect again.
    /// </summary>
    public async Task StartOverAsync(string url)
    {
        if (_disposed) return;
        // Read before anything is awaited: this is the history as the session left it.
        var pages = _stack.Count - 1;
        try
        {
            var target = Normalize(url);
            if (_overlays.Count > 0) await CloseAllOverlaysAsync();
            if (pages > 0) await _js.HistoryRewindAsync(pages);
            SetPending(PendingKind.Reset);
            _nav.NavigateTo(target, new NavigationOptions { ReplaceHistoryEntry = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Starting the history over failed");
        }
    }

    /// <summary>
    /// Web: loads the page again (a new web client was deployed). Overlays close first, so no stale overlay entry
    /// stays in the history; the current address is kept, so the same chat or screen opens.
    /// </summary>
    public async Task ReloadAppAsync()
    {
        if (_disposed) return;
        try
        {
            if (_overlays.Count > 0) await CloseAllOverlaysAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Closing overlays before the reload failed");
        }
        _nav.Refresh(forceReload: true);
    }

    public void OpenUser(Guid userId) => NavigateTo($"/user/{userId}");

    public void OpenUsername(string username) => NavigateTo(DeepLinks.UserProfilePath(Uri.EscapeDataString(username.TrimStart('@'))));

    public void OpenChatInfo(Guid chatId) => NavigateTo($"/chat/{chatId}/info");

    /// <summary>Tab bar / icon rail: switches area without building a deep history.</summary>
    public void GoToSection(ShellSection section)
    {
        var target = section switch
        {
            ShellSection.Contacts => "/contacts",
            ShellSection.Calls => "/calls",
            ShellSection.Settings => "/settings",
            _ => "/",
        };
        if (CurrentPath == target)
        {
            Section = section;
            RaiseChanged();
            return;
        }
        // Chats is the root: other areas sit one Back above it, switching between them replaces.
        if (target == "/")
        {
            if (_stack.Count >= 2 && PathOf(_stack[^2]) == "/" && IsTopLevel(CurrentPath)) _ = GoBackAsync("/");
            else NavigateTo("/", replace: true);
            return;
        }
        NavigateTo(target, replace: CurrentPath != "/");
    }

    // ---- Deep links ----

    /// <summary>
    /// Opens our own links in-app (skymessage://…, https://server/u/…, /join/…, /chat/…). Signed-out users are
    /// sent to the welcome screen and the link opens after sign-in. False when the URI is not one of ours.
    /// </summary>
    public bool HandleDeepLink(Uri uri)
    {
        if (!DeepLinks.TryParse(uri, out var target) || target is null) return false;
        var path = target.Kind switch
        {
            DeepLinkKind.User => DeepLinks.UserProfilePath(Uri.EscapeDataString(target.Value)),
            DeepLinkKind.Join => DeepLinks.JoinChatPath(Uri.EscapeDataString(target.Value)),
            DeepLinkKind.Chat when Guid.TryParse(target.Value, out var chatId) => DeepLinks.ChatPath(chatId),
            _ => null,
        };
        if (path is null) return false;
        if (_auth.State != AuthState.SignedIn)
        {
            RememberDeepLink(path);
            if (!IsAnonymousPath(CurrentPath)) NavigateTo(WelcomePath, replace: true);
            return true;
        }
        if (path != CurrentUrl) NavigateTo(path);
        return true;
    }

    /// <summary>True for links the app understands (profile, invite, chat), whatever the host.</summary>
    public static bool IsAppLink(Uri uri) => DeepLinks.TryParse(uri, out var target) && target is not null;

    /// <summary>Remembers a protected link opened while signed out (only profile, invite and chat links).</summary>
    public void RememberDeepLink(string url)
    {
        var path = PathOf(Normalize(url));
        if (path.StartsWith("/u/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/join/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/chat/", StringComparison.OrdinalIgnoreCase))
            _pendingDeepLink = Normalize(url);
    }

    /// <summary>Where to go right after sign-in: the pending deep link (consumed) or the chat list.</summary>
    public string TakePostSignInTarget()
    {
        var target = _pendingDeepLink ?? "/";
        _pendingDeepLink = null;
        return target;
    }

    // ---- Overlays ----

    /// <summary>
    /// Pushes a history entry for an overlay. <paramref name="onDismiss"/> runs when Back/Escape (or a
    /// navigation) closes it: close the UI there without touching the handle.
    /// </summary>
    public async Task<OverlayHandle> OpenOverlayAsync(Func<Task> onDismiss)
    {
        var id = Guid.NewGuid().ToString("N");
        var handle = new OverlayHandle(this, id);
        _overlays.Add(new OverlayEntry(id, onDismiss, handle));
        RaiseChanged();
        await _js.HistoryPushAsync(id);
        return handle;
    }

    public Task<OverlayHandle> OpenOverlayAsync(Action onDismiss) => OpenOverlayAsync(() =>
    {
        onDismiss();
        return Task.CompletedTask;
    });

    internal async Task CloseOverlayAsync(OverlayHandle handle)
    {
        var index = _overlays.FindIndex(o => o.Id == handle.Id);
        if (index < 0)
        {
            handle.MarkClosed();
            return;
        }
        var above = _overlays.Skip(index + 1).Reverse().ToList();
        _overlays.RemoveRange(index, _overlays.Count - index);
        handle.MarkClosed();
        foreach (var entry in above)
        {
            if (entry.Handle.MarkClosed()) await DismissSafeAsync(entry);
        }
        await _js.HistoryCloseAsync(handle.Id);
        RaiseChanged();
    }

    /// <summary>Called by app.js when Back/Escape popped overlay entries (top first).</summary>
    [JSInvokable]
    public async Task OnOverlaysPopped(string[] ids)
    {
        foreach (var id in ids)
        {
            var index = _overlays.FindIndex(o => o.Id == id);
            if (index < 0) continue;
            var entry = _overlays[index];
            _overlays.RemoveAt(index);
            if (entry.Handle.MarkClosed()) await DismissSafeAsync(entry);
        }
        RaiseChanged();
    }

    /// <summary>Closes every overlay (UI and history entries), e.g. before navigating or on sign-out.</summary>
    public async Task CloseAllOverlaysAsync()
    {
        if (_overlays.Count == 0) return;
        var bottom = _overlays[0];
        var all = _overlays.AsEnumerable().Reverse().ToList();
        _overlays.Clear();
        foreach (var entry in all)
        {
            if (entry.Handle.MarkClosed()) await DismissSafeAsync(entry);
        }
        await _js.HistoryCloseAsync(bottom.Id);
        RaiseChanged();
    }

    private async Task DismissSafeAsync(OverlayEntry entry)
    {
        try
        {
            await entry.OnDismiss();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Overlay dismiss failed");
        }
    }

    // ---- Location tracking ----

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var url = Relative(e.Location);
        var kind = _pendingKind;
        if (kind != PendingKind.None && DateTimeOffset.UtcNow - _pendingAt > PendingWindow) kind = PendingKind.None;
        _pendingKind = PendingKind.None;
        _requested = null;

        switch (kind)
        {
            case PendingKind.Push:
                Push(url);
                LastDirection = NavDirection.Forward;
                break;
            case PendingKind.Replace:
                if (_stack.Count == 0) _stack.Add(url);
                else _stack[^1] = url;
                LastDirection = NavDirection.None;
                break;
            case PendingKind.Back:
                PopTo(url);
                LastDirection = NavDirection.Back;
                break;
            case PendingKind.Reset:
                _stack.Clear();
                _stack.Add(url);
                LastDirection = NavDirection.None;
                break;
            default:
                if (_stack.Count >= 2 && _stack[^2] == url)
                {
                    _stack.RemoveAt(_stack.Count - 1);
                    LastDirection = NavDirection.Back;
                }
                else if (_stack.Count > 0 && _stack[^1] == url)
                {
                    LastDirection = NavDirection.None;
                }
                else
                {
                    Push(url);
                    LastDirection = NavDirection.Forward;
                }
                break;
        }

        CurrentUrl = url;
        if (SectionOf(CurrentPath) is { } section) Section = section;

        if (_overlays.Count > 0)
        {
            // A navigation we did not start (a link inside a sheet, browser forward): close the overlays'
            // UI and forget their (now stale) history entries.
            var all = _overlays.AsEnumerable().Reverse().ToList();
            _overlays.Clear();
            _dispatcher.Post(async () =>
            {
                foreach (var entry in all)
                {
                    if (entry.Handle.MarkClosed()) await DismissSafeAsync(entry);
                }
                await _js.HistoryForgetAsync();
            });
        }
        RaiseChanged();
    }

    private void Push(string url)
    {
        _stack.Add(url);
        if (_stack.Count > MaxStack) _stack.RemoveAt(0);
    }

    private void PopTo(string url)
    {
        if (_stack.Count > 0) _stack.RemoveAt(_stack.Count - 1);
        var index = _stack.LastIndexOf(url);
        if (index >= 0) _stack.RemoveRange(index + 1, _stack.Count - index - 1);
        else _stack.Add(url);
    }

    private void SetPending(PendingKind kind)
    {
        _pendingKind = kind;
        _pendingAt = DateTimeOffset.UtcNow;
    }

    private void OnDeepLinkReceived(Uri uri) => _dispatcher.Post(() => HandleDeepLink(uri));

    private void OnAuthChanged()
    {
        if (_auth.State != AuthState.SignedOut) return;
        // The in-app stack is not cleared here: the auth gate's redirect (StartOverAsync) still needs it to drop the
        // signed-in pages from the browser history, and starts it over itself.
        _dispatcher.Post(CloseAllOverlaysAsync);
    }

    private void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Navigation subscriber failed"); }
        }
    }

    // ---- Route helpers ----

    /// <summary>"/welcome", "/login", "/register" (signed-out screens).</summary>
    public static bool IsAnonymousPath(string path) =>
        path.Equals(WelcomePath, StringComparison.OrdinalIgnoreCase)
        || path.Equals("/login", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/register", StringComparison.OrdinalIgnoreCase);

    /// <summary>Screens that show the bottom tab bar on phones.</summary>
    public static bool IsTopLevel(string path) => path is "/" or "/contacts" or "/calls" or "/settings";

    /// <summary>Chat id of "/chat/{id}", "/chat/{id}/info" and "/chat/{id}/media".</summary>
    public static Guid? ChatIdOf(string path)
    {
        if (!path.StartsWith("/chat/", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = path[6..];
        var slash = rest.IndexOf('/');
        var idPart = slash < 0 ? rest : rest[..slash];
        return Guid.TryParse(idPart, out var id) ? id : null;
    }

    /// <summary>"info" or "media" for chat sub-pages, null for the chat itself.</summary>
    public static string? ChatSubPageOf(string path)
    {
        if (ChatIdOf(path) is null) return null;
        var rest = path[6..];
        var slash = rest.IndexOf('/');
        return slash < 0 ? null : rest[(slash + 1)..].TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>Area a path belongs to; null for neutral pages (profiles, search, invites) that keep the current area.</summary>
    public static ShellSection? SectionOf(string path)
    {
        if (path == "/" || path.StartsWith("/chat/", StringComparison.OrdinalIgnoreCase)) return ShellSection.Chats;
        if (path.StartsWith("/contacts", StringComparison.OrdinalIgnoreCase)) return ShellSection.Contacts;
        if (path.StartsWith("/calls", StringComparison.OrdinalIgnoreCase)) return ShellSection.Calls;
        if (path.StartsWith("/settings", StringComparison.OrdinalIgnoreCase)) return ShellSection.Settings;
        return null;
    }

    public static string PathOf(string url)
    {
        var cut = url.IndexOfAny(['?', '#']);
        var path = cut < 0 ? url : url[..cut];
        if (path.Length == 0) return "/";
        if (path.Length > 1) path = path.TrimEnd('/');
        return path.StartsWith('/') ? path : "/" + path;
    }

    private string Relative(string absolute)
    {
        try
        {
            var relative = _nav.ToBaseRelativePath(absolute);
            return "/" + relative;
        }
        catch (ArgumentException)
        {
            return "/";
        }
    }

    private string Normalize(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            try
            {
                return "/" + _nav.ToBaseRelativePath(absolute.ToString());
            }
            catch (ArgumentException)
            {
                return absolute.PathAndQuery;
            }
        }
        return url.StartsWith('/') ? url : "/" + url;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _nav.LocationChanged -= OnLocationChanged;
        _deepLinks.DeepLinkReceived -= OnDeepLinkReceived;
        _auth.Changed -= OnAuthChanged;
        _self?.Dispose();
        return ValueTask.CompletedTask;
    }
}
