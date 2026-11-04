using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>What one sync did.</summary>
/// <param name="Added">People added to my contacts.</param>
/// <param name="Announced">Of those, the ones announced as new on SkyMessage.</param>
/// <param name="Removed">Synced contacts whose number left the phone book.</param>
/// <param name="Renamed">Synced contacts that took their new phone-book name.</param>
/// <param name="LookedUp">The numbers went to the server this time (else only the phone book was compared).</param>
public sealed record ContactSyncResult(int Added, IReadOnlyList<PhoneBookMatch> Announced, int Removed = 0, int Renamed = 0, bool LookedUp = false)
{
    public static ContactSyncResult None { get; } = new(0, []);
}

/// <summary>
/// Phone-book sync (<see cref="ContactsSettings.SyncPhoneContacts"/>, per device): with the contacts permission it reads
/// the phone book at every start and when it changes (<see cref="IDeviceContactsWatcher"/>; those syncs at least
/// <see cref="ContactSyncService.WatcherSpacing"/> apart), keeps my synced contacts in step with it and adds the people
/// it finds there, marked as synced. The numbers go to the server (find-by-phones, rate limited) only when the set of
/// numbers changed, and otherwise once a day to find people who joined; after a 429 it waits for the server's window.
/// A synced contact whose number left the phone book is removed, and one renamed there takes the new name (people the
/// user added or renamed by hand are never touched). The first sync, and the first after turning sync on, adds people
/// quietly; later ones announce, once, people whose number was already in the phone book at the previous look-up —
/// they really just appeared ("X теперь в SkyMessage") — while a number saved just now is added quietly. Someone removed
/// by hand is not brought back. It never asks for the permission by itself — only <see cref="SetEnabledAsync"/> does.
/// </summary>
public interface IContactSyncService
{
    /// <summary>This device has a phone book to read.</summary>
    bool IsSupported { get; }
    bool IsEnabled { get; }
    bool IsSyncing { get; }
    /// <summary>The last sync could not read the phone book (the permission was taken away).</summary>
    bool IsBlocked { get; }
    /// <summary>On: asks for the contacts permission (false when refused, sync stays off) and syncs. Off: stops.</summary>
    Task<bool> SetEnabledAsync(bool enabled);
    /// <summary>Syncs now when enabled (else does nothing); the server is asked only when its rules say so.</summary>
    Task<ContactSyncResult> SyncNowAsync(CancellationToken cancellationToken = default);
    /// <summary>Removes the synced contacts from my account, turns sync off and forgets whom it found.</summary>
    Task<int> RemoveSyncedAsync();
    event Action? Changed;
}

/// <summary>What sync remembers per account (<see cref="IAccountLocalStore"/>); numbers only as short hashes.</summary>
internal sealed class ContactSyncState
{
    /// <summary>Everyone sync ever found: someone removed by hand is not added back.</summary>
    public HashSet<Guid> Known { get; set; } = [];
    /// <summary>The number (<see cref="ContactSyncService.NumberKey"/>) each synced contact was found by.</summary>
    public Dictionary<Guid, string> FoundBy { get; set; } = [];
    /// <summary>The phone book's numbers at the last look-up (who was there already tells who really just joined).</summary>
    public HashSet<string> Numbers { get; set; } = [];
    /// <summary>Hash of the numbers at the last look-up: the same set needs no new one until a day has passed.</summary>
    public string? NumbersHash { get; set; }
    public DateTimeOffset? LookedUpAt { get; set; }
    /// <summary>No look-up before this (the server said "too many requests").</summary>
    public DateTimeOffset? PausedUntil { get; set; }
    /// <summary>The next look-up adds quietly (sync was just turned on).</summary>
    public bool Quiet { get; set; }
}

internal sealed class ContactSyncService : IContactSyncService, IAccountScoped
{
    internal const string StateKey = "contacts.sync.state";
    /// <summary>A burst of phone-book changes settles first.</summary>
    internal static readonly TimeSpan ChangeDelay = TimeSpan.FromSeconds(5);
    /// <summary>Syncs started by phone-book changes (sync adapters write often) stay this far apart.</summary>
    internal static readonly TimeSpan WatcherSpacing = TimeSpan.FromMinutes(30);
    /// <summary>An unchanged phone book still goes to the server this often, to find people who joined.</summary>
    internal static readonly TimeSpan FullCheckEvery = TimeSpan.FromDays(1);
    /// <summary>After this long without a look-up nobody is announced (they may have joined long ago).</summary>
    internal static readonly TimeSpan AnnounceWithin = TimeSpan.FromDays(14);
    /// <summary>The server's look-up window (<see cref="Limits.PhoneLookupWindowMinutes"/>) and a little more.</summary>
    internal static readonly TimeSpan RateLimitPause = TimeSpan.FromMinutes(Limits.PhoneLookupWindowMinutes) + TimeSpan.FromSeconds(30);

    private enum Reason
    {
        /// <summary>App start, sign-in, a caller.</summary>
        Requested,
        /// <summary>Sync was just turned on: look up now, add quietly.</summary>
        TurnedOn,
        /// <summary>The phone book changed, the daily check is due or a pause ended.</summary>
        Scheduled,
    }

    private readonly IContactStore _contacts;
    private readonly IDeviceContactsService _device;
    private readonly IDeviceContactsWatcher _watcher;
    private readonly IPermissionService _permissions;
    private readonly ISettingsService _settings;
    private readonly IAccountLocalStore _local;
    private readonly IReminderNotificationService _reminders;
    private readonly AccountLifecycle _lifecycle;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<ContactSyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private volatile bool _syncing;
    private volatile bool _blocked;
    private bool _watching;
    private DateTimeOffset? _lastSyncAt;
    private DateTimeOffset _laterAt;
    private CancellationTokenSource? _later;

    public ContactSyncService(
        IContactStore contacts,
        IDeviceContactsService device,
        IDeviceContactsWatcher watcher,
        IPermissionService permissions,
        ISettingsService settings,
        IAccountLocalStore local,
        IReminderNotificationService reminders,
        AccountLifecycle lifecycle,
        IServiceProvider services,
        TimeProvider time,
        ILogger<ContactSyncService> logger)
    {
        _contacts = contacts;
        _device = device;
        _watcher = watcher;
        _permissions = permissions;
        _settings = settings;
        _local = local;
        _reminders = reminders;
        _lifecycle = lifecycle;
        _services = services;
        _time = time;
        _logger = logger;
        watcher.Changed += OnPhoneBookChanged;
        settings.Changed += ApplyWatcher;
        lifecycle.Register(this);
    }

    public bool IsSupported => _device.IsSupported;

    public bool IsEnabled => IsSupported && _settings.Current.Contacts.SyncPhoneContacts;

    public bool IsSyncing => _syncing;

    public bool IsBlocked => _blocked;

    public event Action? Changed;

    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            if (!IsSupported || !await _device.RequestPermissionAsync().ConfigureAwait(false)) return false;
            _blocked = false;
            await _settings.UpdateAsync(s => s.Contacts.SyncPhoneContacts = true).ConfigureAwait(false);
            EventRaiser.Raise(Changed, _logger);
            SyncAsync(Reason.TurnedOn, CancellationToken.None).Forget(_logger);
            return true;
        }
        CancelLater();
        await _settings.UpdateAsync(s => s.Contacts.SyncPhoneContacts = false).ConfigureAwait(false);
        EventRaiser.Raise(Changed, _logger);
        return true;
    }

    public Task<ContactSyncResult> SyncNowAsync(CancellationToken cancellationToken = default) => SyncAsync(Reason.Requested, cancellationToken);

    private async Task<ContactSyncResult> SyncAsync(Reason reason, CancellationToken cancellationToken)
    {
        if (!IsEnabled || _lifecycle.ActiveUserId is not { } account) return ContactSyncResult.None;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsEnabled || _lifecycle.ActiveUserId != account) return ContactSyncResult.None;
            // Never a permission dialog out of the blue: only a granted permission is used.
            if (await _permissions.CheckAsync(AppPermission.Contacts).ConfigureAwait(false) != PermissionState.Granted)
            {
                SetBlocked(true);
                return ContactSyncResult.None;
            }
            SetBlocked(false);
            SetSyncing(true);

            await _contacts.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var book = await _contacts.ReadPhoneBookAsync(cancellationToken).ConfigureAwait(false);
            if (_lifecycle.ActiveUserId != account || !IsEnabled) return ContactSyncResult.None;
            var now = _time.GetUtcNow();
            lock (_lock) _lastSyncAt = now;

            var state = await _local.GetAsync<ContactSyncState>(StateKey).ConfigureAwait(false) ?? new ContactSyncState();
            if (reason == Reason.TurnedOn) state.Quiet = true;
            // Number key → the entry (a number written twice counts once).
            var numbers = new Dictionary<string, PhoneBookEntry>(StringComparer.Ordinal);
            foreach (var entry in book) numbers.TryAdd(NumberKey(entry.Number), entry);
            var numbersHash = SetHash(numbers.Keys);

            var removed = await RemoveGoneAsync(state, numbers).ConfigureAwait(false);

            var lookUp = reason == Reason.TurnedOn || state.NumbersHash != numbersHash || state.LookedUpAt is not { } last || now - last >= FullCheckEvery;
            if (lookUp && state.PausedUntil is { } paused && paused > now)
            {
                lookUp = false;
                Later(paused);
            }
            if (!lookUp)
            {
                var renamedHere = await RenameFromPhoneBookAsync(state, numbers).ConfigureAwait(false);
                await _local.SetAsync(StateKey, state).ConfigureAwait(false);
                if (state.LookedUpAt is { } at) Later(at + FullCheckEvery);
                return new ContactSyncResult(0, [], removed, renamedHere);
            }

            IReadOnlyList<PhoneBookMatch> matches;
            try
            {
                matches = await _contacts.MatchPhoneBookAsync(book, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.StatusCode == 429 || ex.Code == ErrorCodes.RateLimited)
            {
                // The import shares the quota: wait for the window to end instead of asking again on the next change.
                state.PausedUntil = now + RateLimitPause;
                await _local.SetAsync(StateKey, state).ConfigureAwait(false);
                Later(state.PausedUntil.Value);
                _logger.LogInformation("Contact sync paused: too many phone look-ups");
                return new ContactSyncResult(0, [], removed);
            }
            if (_lifecycle.ActiveUserId != account || !IsEnabled) return ContactSyncResult.None;

            var announce = !state.Quiet && state.LookedUpAt is { } previous && now - previous <= AnnounceWithin;
            var before = state.Numbers;
            var wasContact = _contacts.Contacts.Select(c => c.User.Id).ToHashSet();
            // One match per person: the first number found for them (their name in the phone book) counts.
            var people = matches.GroupBy(m => m.User.Id).Select(g => g.First()).ToList();
            var fresh = people
                .Where(m => !m.AlreadyContact && !wasContact.Contains(m.User.Id) && !_contacts.IsBlocked(m.User.Id) && !state.Known.Contains(m.User.Id))
                .ToList();
            var synced = SyncedContacts();
            var renames = people.Where(m => synced.TryGetValue(m.User.Id, out var contact) && NameChanged(contact, m.DeviceContactName)).ToList();

            var sent = fresh.Concat(renames).ToList();
            var result = sent.Count == 0 ? [] : await _contacts.AddSyncedAsync(sent).ConfigureAwait(false);
            var addedIds = result.Where(c => !wasContact.Contains(c.User.Id)).Select(c => c.User.Id).ToHashSet();
            var renamed = result.Count(c => wasContact.Contains(c.User.Id));

            foreach (var match in people) state.Known.Add(match.User.Id);
            var nowSynced = SyncedContacts();
            foreach (var match in people)
                if (nowSynced.ContainsKey(match.User.Id))
                    state.FoundBy[match.User.Id] = NumberKey(match.PhoneNumber);
            foreach (var id in state.FoundBy.Keys.Where(id => !nowSynced.ContainsKey(id)).ToList()) state.FoundBy.Remove(id);
            state.Numbers = numbers.Keys.ToHashSet(StringComparer.Ordinal);
            state.NumbersHash = numbersHash;
            state.LookedUpAt = now;
            state.PausedUntil = null;
            state.Quiet = false;
            await _local.SetAsync(StateKey, state).ConfigureAwait(false);
            Later(now + FullCheckEvery);

            // "Теперь в SkyMessage" only for a number that was in the phone book already: a number saved just now
            // belongs to someone who may have been here for years.
            var announced = announce
                ? fresh.Where(m => addedIds.Contains(m.User.Id) && before.Contains(NumberKey(m.PhoneNumber))).ToList()
                : [];
            foreach (var match in announced) await AnnounceAsync(match).ConfigureAwait(false);
            if (addedIds.Count > 0 || renamed > 0 || removed > 0)
                _logger.LogInformation("Contact sync: {Added} added, {Renamed} renamed, {Removed} removed", addedIds.Count, renamed, removed);
            return new ContactSyncResult(addedIds.Count, announced, removed, renamed, LookedUp: true);
        }
        catch (Exception ex) when (ex is ApiException or OperationCanceledException)
        {
            _logger.LogInformation("Contact sync did not finish ({Error})", (ex as ApiException)?.Code ?? "cancelled");
            return ContactSyncResult.None;
        }
        finally
        {
            SetSyncing(false);
            _gate.Release();
        }
    }

    /// <summary>
    /// Synced contacts whose number is no longer in the phone book. Not when the phone book reads empty or most of them
    /// would go at once — that is more likely a phone book being reloaded than a person deleting their contacts.
    /// </summary>
    private async Task<int> RemoveGoneAsync(ContactSyncState state, IReadOnlyDictionary<string, PhoneBookEntry> numbers)
    {
        var synced = SyncedContacts();
        var gone = state.FoundBy.Where(p => synced.ContainsKey(p.Key) && !numbers.ContainsKey(p.Value)).Select(p => p.Key).ToList();
        if (gone.Count == 0) return 0;
        if (numbers.Count == 0 || gone.Count > Math.Max(3, synced.Count / 2))
        {
            _logger.LogInformation("Contact sync left {Count} synced contacts in place: too many numbers went at once", gone.Count);
            return 0;
        }
        var removed = await _contacts.RemoveSyncedAsync(gone).ConfigureAwait(false);
        foreach (var id in removed)
        {
            state.FoundBy.Remove(id);
            // Their number may come back, and then so may they.
            state.Known.Remove(id);
        }
        return removed.Count;
    }

    /// <summary>Without a look-up: synced contacts take the name their number has in the phone book now.</summary>
    private async Task<int> RenameFromPhoneBookAsync(ContactSyncState state, IReadOnlyDictionary<string, PhoneBookEntry> numbers)
    {
        var synced = SyncedContacts();
        var renames = new List<PhoneBookMatch>();
        foreach (var (userId, key) in state.FoundBy)
        {
            if (!synced.TryGetValue(userId, out var contact) || !numbers.TryGetValue(key, out var entry) || !NameChanged(contact, entry.Name)) continue;
            renames.Add(new PhoneBookMatch(entry.Name, entry.Number, contact.User, AlreadyContact: true));
        }
        if (renames.Count == 0) return 0;
        return (await _contacts.AddSyncedAsync(renames).ConfigureAwait(false)).Count;
    }

    private Dictionary<Guid, ContactDto> SyncedContacts() => _contacts.Contacts.Where(c => c.IsSynced).ToDictionary(c => c.User.Id);

    private static bool NameChanged(ContactDto contact, string phoneBookName)
    {
        var name = phoneBookName.Trim();
        if (name.Length > Limits.ContactNameMaxLength) name = name[..Limits.ContactNameMaxLength];
        return name.Length > 0 && name != contact.CustomName;
    }

    public async Task<int> RemoveSyncedAsync()
    {
        CancelLater();
        await _settings.UpdateAsync(s => s.Contacts.SyncPhoneContacts = false).ConfigureAwait(false);
        await _local.RemoveAsync(StateKey).ConfigureAwait(false);
        var removed = await _contacts.RemoveSyncedAsync().ConfigureAwait(false);
        EventRaiser.Raise(Changed, _logger);
        return removed;
    }

    Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        lock (_lock) _lastSyncAt = null;
        ApplyWatcher();
        // After the contacts' own start-up load; the sync waits for it anyway.
        SyncNowAsync().Forget(_logger);
        return Task.CompletedTask;
    }

    Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        CancelLater();
        SetWatching(false);
        _blocked = false;
        return Task.CompletedTask;
    }

    /// <summary>A phone-book change: the next sync comes once the burst settled and the last sync is far enough back.</summary>
    private void OnPhoneBookChanged()
    {
        if (!IsEnabled || _lifecycle.ActiveUserId is null) return;
        var now = _time.GetUtcNow();
        DateTimeOffset? last;
        lock (_lock) last = _lastSyncAt;
        var at = now + ChangeDelay;
        if (last is { } previous && previous + WatcherSpacing > at) at = previous + WatcherSpacing;
        Later(at);
    }

    /// <summary>One sync later (the earliest asked for wins; it reads the phone book as it is then).</summary>
    private void Later(DateTimeOffset at)
    {
        CancellationToken token;
        lock (_lock)
        {
            if (_later is { IsCancellationRequested: false } && _laterAt <= at) return;
            _later?.Cancel();
            _later = new CancellationTokenSource();
            _laterAt = at;
            token = _later.Token;
        }
        RunLaterAsync(at, token).Forget(_logger);
    }

    private async Task RunLaterAsync(DateTimeOffset at, CancellationToken token)
    {
        var wait = at - _time.GetUtcNow();
        try
        {
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (_lock)
        {
            if (token.IsCancellationRequested) return;
            _later = null;
        }
        await SyncAsync(Reason.Scheduled, CancellationToken.None).ConfigureAwait(false);
    }

    private void CancelLater()
    {
        lock (_lock)
        {
            _later?.Cancel();
            _later = null;
        }
    }

    private void ApplyWatcher() => SetWatching(IsEnabled && _lifecycle.ActiveUserId is not null);

    private void SetWatching(bool on)
    {
        if (!_watcher.IsSupported) return;
        lock (_lock)
        {
            if (_watching == on) return;
            _watching = on;
        }
        try
        {
            _watcher.SetEnabled(on);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Phone book watcher not switched");
        }
    }

    private async Task AnnounceAsync(PhoneBookMatch match)
    {
        if (!_reminders.IsSupported) return;
        var texts = _services.GetService<IReminderTexts>() ?? EnglishReminderTexts.Instance;
        var link = string.IsNullOrEmpty(match.User.Username)
            ? null
            : new Uri($"{DeepLinks.AppScheme}://u/{Uri.EscapeDataString(match.User.Username)}");
        await _reminders.ShowAsync(new ReminderNotification($"joined:{match.User.Id:N}", texts.JoinedTitle(match.DeviceContactName),
            texts.JoinedBody(), link)).ConfigureAwait(false);
    }

    /// <summary>A short hash of the number's digits (and a leading +): stored instead of the number itself.</summary>
    internal static string NumberKey(string number)
    {
        var text = number.Trim();
        var digits = new StringBuilder(text.Length);
        if (text.StartsWith('+')) digits.Append('+');
        foreach (var c in text)
            if (char.IsAsciiDigit(c)) digits.Append(c);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digits.ToString())), 0, 8);
    }

    private static string SetHash(IEnumerable<string> keys) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', keys.Order(StringComparer.Ordinal)))), 0, 16);

    private void SetSyncing(bool syncing)
    {
        if (_syncing == syncing) return;
        _syncing = syncing;
        EventRaiser.Raise(Changed, _logger);
    }

    private void SetBlocked(bool blocked)
    {
        if (_blocked == blocked) return;
        _blocked = blocked;
        EventRaiser.Raise(Changed, _logger);
    }
}
