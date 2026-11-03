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
/// <param name="Announced">Of those, the ones announced as new on SkyMessage (none on the first sync).</param>
public sealed record ContactSyncResult(int Added, IReadOnlyList<PhoneBookMatch> Announced)
{
    public static ContactSyncResult None { get; } = new(0, []);
}

/// <summary>
/// Phone-book sync (<see cref="ContactsSettings.SyncPhoneContacts"/>, per device): with the contacts permission it reads
/// the phone book at every start and when it changes (<see cref="IDeviceContactsWatcher"/>), matches it like the import
/// does and adds the people found to my contacts, marked as synced. The first sync adds everyone quietly; later ones
/// add only people sync has never found before and announce each once ("X теперь в SkyMessage"), so someone removed
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
    /// <summary>Syncs now when enabled (else does nothing).</summary>
    Task<ContactSyncResult> SyncNowAsync(CancellationToken cancellationToken = default);
    /// <summary>Removes the synced contacts from my account, turns sync off and forgets whom it found.</summary>
    Task<int> RemoveSyncedAsync();
    event Action? Changed;
}

internal sealed class ContactSyncService : IContactSyncService, IAccountScoped
{
    internal const string KnownKey = "contacts.sync.known";
    private static readonly TimeSpan ChangeDelay = TimeSpan.FromSeconds(5);

    private readonly IContactStore _contacts;
    private readonly IDeviceContactsService _device;
    private readonly IDeviceContactsWatcher _watcher;
    private readonly IPermissionService _permissions;
    private readonly ISettingsService _settings;
    private readonly IAccountLocalStore _local;
    private readonly IReminderNotificationService _reminders;
    private readonly AccountLifecycle _lifecycle;
    private readonly IServiceProvider _services;
    private readonly ILogger<ContactSyncService> _logger;
    private readonly CoalescingAction _onDeviceChange;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _watchLock = new();
    private volatile bool _syncing;
    private volatile bool _blocked;
    private bool _watching;

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
        _logger = logger;
        _onDeviceChange = new CoalescingAction(ChangeDelay, ct => SyncNowAsync(ct), time, logger);
        watcher.Changed += _onDeviceChange.Request;
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
            SyncNowAsync().Forget(_logger);
            return true;
        }
        await _settings.UpdateAsync(s => s.Contacts.SyncPhoneContacts = false).ConfigureAwait(false);
        EventRaiser.Raise(Changed, _logger);
        return true;
    }

    public async Task<ContactSyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || _lifecycle.ActiveUserId is not { } account) return ContactSyncResult.None;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Never a permission dialog out of the blue: only a granted permission is used.
            if (await _permissions.CheckAsync(AppPermission.Contacts).ConfigureAwait(false) != PermissionState.Granted)
            {
                SetBlocked(true);
                return ContactSyncResult.None;
            }
            SetBlocked(false);
            SetSyncing(true);

            await _contacts.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var matches = await _contacts.FindDeviceContactsAsync(cancellationToken).ConfigureAwait(false);
            if (_lifecycle.ActiveUserId != account || !IsEnabled) return ContactSyncResult.None;

            var known = await _local.GetAsync<HashSet<Guid>>(KnownKey).ConfigureAwait(false);
            var firstSync = known is null;
            known ??= [];
            var fresh = matches
                .Where(m => !m.AlreadyContact && !_contacts.IsContact(m.User.Id) && !_contacts.IsBlocked(m.User.Id) && !known.Contains(m.User.Id))
                .GroupBy(m => m.User.Id)
                .Select(g => g.First())
                .ToList();

            var added = fresh.Count == 0 ? [] : await _contacts.AddSyncedAsync(fresh).ConfigureAwait(false);
            foreach (var match in matches) known.Add(match.User.Id);
            await _local.SetAsync(KnownKey, known).ConfigureAwait(false);

            var addedIds = added.Select(c => c.User.Id).ToHashSet();
            var announced = firstSync ? [] : fresh.Where(m => addedIds.Contains(m.User.Id)).ToList();
            foreach (var match in announced) await AnnounceAsync(match).ConfigureAwait(false);
            if (added.Count > 0) _logger.LogInformation("Contact sync added {Count} people", added.Count);
            return new ContactSyncResult(added.Count, announced);
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

    public async Task<int> RemoveSyncedAsync()
    {
        await _settings.UpdateAsync(s => s.Contacts.SyncPhoneContacts = false).ConfigureAwait(false);
        await _local.RemoveAsync(KnownKey).ConfigureAwait(false);
        var removed = await _contacts.RemoveSyncedAsync().ConfigureAwait(false);
        EventRaiser.Raise(Changed, _logger);
        return removed;
    }

    Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        ApplyWatcher();
        // After the contacts' own start-up load; the sync waits for it anyway.
        SyncNowAsync().Forget(_logger);
        return Task.CompletedTask;
    }

    Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        _onDeviceChange.Cancel();
        SetWatching(false);
        _blocked = false;
        return Task.CompletedTask;
    }

    private void ApplyWatcher() => SetWatching(IsEnabled && _lifecycle.ActiveUserId is not null);

    private void SetWatching(bool on)
    {
        if (!_watcher.IsSupported) return;
        lock (_watchLock)
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
