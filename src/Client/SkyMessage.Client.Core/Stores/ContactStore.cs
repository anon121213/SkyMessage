using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// Contacts and blocks (cached per account as "contacts", reloaded on ContactsChanged and after resync) plus the
/// phone-book import: device numbers are matched in batches with the regions of the SIM/network, my own phone and
/// the device locale tried in order for numbers without '+'. Only matches the user confirms are imported.
/// </summary>
internal sealed class ContactStore : IContactStore, IAccountScoped
{
    private const string CacheName = "contacts";

    private readonly ISkyApi _api;
    private readonly IAuthService _auth;
    private readonly IUserStore _users;
    private readonly IDeviceContactsService _deviceContacts;
    private readonly IPlatformInfo _platform;
    private readonly LocalCache _cache;
    private readonly AccountLifecycle _lifecycle;
    private readonly ILogger<ContactStore> _logger;
    private readonly SerialQueue _queue = new();
    private readonly object _lock = new();

    private volatile IReadOnlyList<ContactDto> _contacts = [];
    private volatile IReadOnlyList<UserSummaryDto> _blocked = [];
    private volatile HashSet<Guid> _contactIds = [];
    private volatile HashSet<Guid> _blockedIds = [];
    private volatile bool _isLoaded;
    private volatile bool _loadedFromServer;
    private volatile int _generation;
    private Task? _load;
    private bool _reloadAfterLoad;

    public ContactStore(
        ISkyApi api,
        IRealtimeService realtime,
        IAuthService auth,
        IUserStore users,
        IDeviceContactsService deviceContacts,
        IPlatformInfo platform,
        LocalCache cache,
        AccountLifecycle lifecycle,
        ILogger<ContactStore> logger)
    {
        _api = api;
        _auth = auth;
        _users = users;
        _deviceContacts = deviceContacts;
        _platform = platform;
        _cache = cache;
        _lifecycle = lifecycle;
        _logger = logger;
        realtime.ContactsChanged += _ => LoadAsync(force: true).Forget(_logger);
        realtime.Resynced += () =>
        {
            if (_loadedFromServer) LoadAsync(force: true).Forget(_logger);
        };
        lifecycle.Register(this);
    }

    public IReadOnlyList<ContactDto> Contacts => _contacts;

    public IReadOnlyList<UserSummaryDto> Blocked => _blocked;

    public bool IsLoaded => _isLoaded;

    public event Action? Changed;

    public Task LoadAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && _loadedFromServer) return Task.CompletedTask;
        Task load;
        lock (_lock)
        {
            if (_load is { IsCompleted: false } running)
            {
                if (force) _reloadAfterLoad = true;
                load = running;
            }
            else
            {
                load = _load = LoadCoreAsync(_generation);
            }
        }
        return load.WaitAsync(cancellationToken);
    }

    private async Task LoadCoreAsync(int generation)
    {
        await Task.Yield();
        while (true)
        {
            lock (_lock) _reloadAfterLoad = false;
            try
            {
                var token = _lifecycle.AccountToken;
                var contactsTask = _api.GetContactsAsync(token);
                var blockedTask = _api.GetBlockedAsync(token);
                var contacts = await contactsTask.ConfigureAwait(false);
                var blocked = await blockedTask.ConfigureAwait(false);
                if (generation != _generation) return;
                _users.Upsert(contacts.Select(c => c.User).Concat(blocked));
                await SetAsync(contacts, blocked).ConfigureAwait(false);
                _loadedFromServer = true;
            }
            catch (Exception ex) when (ex is ApiException or OperationCanceledException)
            {
                _logger.LogInformation("Contacts refresh failed ({Error})", (ex as ApiException)?.Code ?? "cancelled");
            }

            lock (_lock)
            {
                if (!_reloadAfterLoad) return;
            }
        }
    }

    public bool IsContact(Guid userId) => _contactIds.Contains(userId);

    public bool IsBlocked(Guid userId) => _blockedIds.Contains(userId);

    public async Task AddAsync(Guid userId, string? customName = null)
    {
        var contact = await _api.AddContactAsync(new AddContactRequest { UserId = userId, CustomName = Clean(customName) }).ConfigureAwait(false);
        _users.Upsert([contact.User]);
        await MutateAsync(list => list.Where(c => c.User.Id != userId).Append(contact).ToList(), null).ConfigureAwait(false);
    }

    public async Task RenameAsync(Guid userId, string? customName)
    {
        var contact = await _api.UpdateContactAsync(userId, new UpdateContactRequest { CustomName = Clean(customName) }).ConfigureAwait(false);
        _users.Upsert([contact.User]);
        await MutateAsync(list => list.Select(c => c.User.Id == userId ? contact : c).ToList(), null).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid userId)
    {
        await _api.RemoveContactAsync(userId).ConfigureAwait(false);
        if (_users.Get(userId) is { } user) _users.Upsert([user with { IsContact = false, ContactName = null }]);
        await MutateAsync(list => list.Where(c => c.User.Id != userId).ToList(), null).ConfigureAwait(false);
    }

    public async Task BlockAsync(Guid userId)
    {
        await _api.BlockAsync(userId).ConfigureAwait(false);
        var user = _users.Get(userId) ?? await _users.GetOrFetchAsync(userId).ConfigureAwait(false);
        await MutateAsync(null, blocked => user is null || blocked.Any(b => b.Id == userId) ? blocked.ToList() : blocked.Append(user).ToList()).ConfigureAwait(false);
    }

    public async Task UnblockAsync(Guid userId)
    {
        await _api.UnblockAsync(userId).ConfigureAwait(false);
        await MutateAsync(null, blocked => blocked.Where(b => b.Id != userId).ToList()).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PhoneBookMatch>> FindDeviceContactsAsync(CancellationToken cancellationToken = default)
    {
        if (!_deviceContacts.IsSupported || !await _deviceContacts.RequestPermissionAsync().ConfigureAwait(false)) return [];
        var deviceContacts = await _deviceContacts.GetContactsAsync(cancellationToken).ConfigureAwait(false);

        // Submitted number → address-book name (E.164 from the OS when available, else the raw entry).
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var contact in deviceContacts)
        {
            var numbers = contact.E164Numbers is { Count: > 0 } normalized ? normalized : contact.PhoneNumbers;
            foreach (var raw in numbers)
            {
                var number = raw?.Trim();
                if (string.IsNullOrEmpty(number) || number.Count(char.IsAsciiDigit) < 5) continue;
                names.TryAdd(number, string.IsNullOrWhiteSpace(contact.DisplayName) ? number : contact.DisplayName.Trim());
            }
        }
        if (names.Count == 0) return [];

        var regions = DefaultRegions();
        var matches = new List<PhoneMatchDto>();
        foreach (var chunk in names.Keys.Chunk(Limits.FindByPhonesMax))
        {
            var found = await _api.FindByPhonesAsync(new FindByPhonesRequest
            {
                PhoneNumbers = chunk,
                DefaultRegion = regions.FirstOrDefault(),
                DefaultRegions = regions,
            }, cancellationToken).ConfigureAwait(false);
            matches.AddRange(found);
        }

        var me = _auth.UserId;
        var result = new List<PhoneBookMatch>();
        var seen = new HashSet<Guid>();
        foreach (var match in matches)
        {
            if (match.User.Id == me || !seen.Add(match.User.Id)) continue;
            var name = names.GetValueOrDefault(match.PhoneNumber) ?? match.User.DisplayName;
            result.Add(new PhoneBookMatch(name, match.PhoneNumber, match.User, IsContact(match.User.Id)));
        }
        _users.Upsert(result.Select(r => r.User));
        return result.OrderBy(r => r.AlreadyContact).ThenBy(r => r.DeviceContactName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task ImportAsync(IReadOnlyList<PhoneBookMatch> confirmedMatches)
    {
        var requests = confirmedMatches
            .Where(m => !m.AlreadyContact && !IsContact(m.User.Id))
            .GroupBy(m => m.User.Id)
            .Select(g => new AddContactRequest { UserId = g.Key, CustomName = Clean(g.First().DeviceContactName) })
            .ToList();
        if (requests.Count == 0) return;

        var added = new List<ContactDto>();
        foreach (var chunk in requests.Chunk(200))
            added.AddRange(await _api.AddContactsBulkAsync(new AddContactsBulkRequest { Contacts = chunk }).ConfigureAwait(false));

        _users.Upsert(added.Select(c => c.User));
        var ids = added.Select(c => c.User.Id).ToHashSet();
        await MutateAsync(list => list.Where(c => !ids.Contains(c.User.Id)).Concat(added).ToList(), null).ConfigureAwait(false);
    }

    async Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        var generation = _generation;
        var cached = await _cache.ReadAsync<CachedContacts>(LocalCache.AccountKey(userId, CacheName)).ConfigureAwait(false);
        if (cached is null || generation != _generation || _isLoaded) return;
        _users.Upsert(cached.Contacts.Select(c => c.User).Concat(cached.Blocked));
        await SetAsync(cached.Contacts, cached.Blocked, persist: false).ConfigureAwait(false);
    }

    async Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        Interlocked.Increment(ref _generation);
        _loadedFromServer = false;
        await _queue.RunAsync(() =>
        {
            _contacts = [];
            _blocked = [];
            _contactIds = [];
            _blockedIds = [];
            _isLoaded = false;
        }).ConfigureAwait(false);
        EventRaiser.Raise(Changed, _logger);
    }

    /// <summary>Regions tried in order for numbers without '+': SIM/network country, my phone's country, device locale.</summary>
    internal IReadOnlyList<string> DefaultRegions()
    {
        var regions = new List<string>();
        void Add(string? region)
        {
            if (PhoneRegions.Normalize(region) is { } r && !regions.Contains(r)) regions.Add(r);
        }
        Add(_platform.CountryCode);
        Add(PhoneRegions.RegionOf(_auth.Me?.PhoneNumber));
        try
        {
            Add(System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName);
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException or InvalidOperationException)
        {
            // Invariant culture (browser without ICU data): no locale region.
        }
        return regions;
    }

    private Task SetAsync(IReadOnlyList<ContactDto> contacts, IReadOnlyList<UserSummaryDto> blocked, bool persist = true) =>
        MutateAsync(_ => contacts.ToList(), _ => blocked.ToList(), persist);

    private async Task MutateAsync(Func<IReadOnlyList<ContactDto>, List<ContactDto>>? contacts,
        Func<IReadOnlyList<UserSummaryDto>, List<UserSummaryDto>>? blocked, bool persist = true)
    {
        var generation = _generation;
        CachedContacts? snapshot = null;
        await _queue.RunAsync(() =>
        {
            if (generation != _generation) return;
            if (contacts is not null)
            {
                var sorted = contacts(_contacts)
                    .OrderBy(c => c.User.ContactName ?? c.CustomName ?? c.User.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(c => c.User.Username, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _contacts = sorted;
                _contactIds = sorted.Select(c => c.User.Id).ToHashSet();
            }
            if (blocked is not null)
            {
                var list = blocked(_blocked);
                _blocked = list;
                _blockedIds = list.Select(b => b.Id).ToHashSet();
            }
            _isLoaded = true;
            snapshot = new CachedContacts { Contacts = [.. _contacts], Blocked = [.. _blocked] };
        }).ConfigureAwait(false);
        if (snapshot is null) return;
        EventRaiser.Raise(Changed, _logger);
        if (persist && _lifecycle.ActiveUserId is { } userId)
            await _cache.WriteAsync(LocalCache.AccountKey(userId, CacheName), snapshot).ConfigureAwait(false);
    }

    private static string? Clean(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > Limits.ContactNameMaxLength ? trimmed[..Limits.ContactNameMaxLength] : trimmed;
    }

    private sealed class CachedContacts
    {
        public List<ContactDto> Contacts { get; set; } = [];
        public List<UserSummaryDto> Blocked { get; set; } = [];
    }
}
