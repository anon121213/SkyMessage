using System.Runtime.CompilerServices;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;

namespace SkyMessage.Client.UI.Features.Media;

/// <summary>
/// Which incoming voice messages and round video notes this account has already played ("media.played.v1" in
/// <see cref="IAccountLocalStore"/>, newest 4000 kept). One instance per account store (per app), cached in memory and
/// reloaded when the signed-in user changes. Events may fire on any thread.
/// </summary>
public sealed class PlayedMediaStore
{
    public const string StorageKey = "media.played.v1";
    private const int MaxItems = 4000;

    private static readonly ConditionalWeakTable<IAccountLocalStore, PlayedMediaStore> Instances = new();

    private readonly IAccountLocalStore _store;
    private readonly IAuthService _auth;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<Guid> _played = [];
    private List<Guid> _order = [];
    private Guid? _loadedFor;
    private Task? _loading;

    private PlayedMediaStore(IAccountLocalStore store, IAuthService auth)
    {
        _store = store;
        _auth = auth;
    }

    /// <summary>Raised with the media id that became played, or <see cref="Guid.Empty"/> after a (re)load.</summary>
    public event Action<Guid>? Changed;

    public static PlayedMediaStore For(IAccountLocalStore store, IAuthService auth) =>
        Instances.GetValue(store, s => new PlayedMediaStore(s, auth));

    /// <summary>The played list of the current account is in memory.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_lock) return _loadedFor is { } user && user == _auth.UserId;
        }
    }

    /// <summary>True only when the list is loaded and the media is not in it (no "new" mark while unknown).</summary>
    public bool IsUnplayed(Guid mediaId)
    {
        if (!IsLoaded)
        {
            _ = EnsureLoadedAsync();
            return false;
        }
        lock (_lock) return !_played.Contains(mediaId);
    }

    public Task EnsureLoadedAsync()
    {
        if (IsLoaded) return Task.CompletedTask;
        lock (_lock)
        {
            if (_loading is { IsCompleted: false } running) return running;
            return _loading = LoadAsync();
        }
    }

    public async Task MarkPlayedAsync(Guid mediaId)
    {
        if (mediaId == Guid.Empty) return;
        await EnsureLoadedAsync();
        List<Guid> snapshot;
        lock (_lock)
        {
            if (!_played.Add(mediaId)) return;
            _order.Add(mediaId);
            if (_order.Count > MaxItems)
            {
                foreach (var old in _order.Take(_order.Count - MaxItems)) _played.Remove(old);
                _order = _order.Skip(_order.Count - MaxItems).ToList();
            }
            snapshot = [.. _order];
        }
        Raise(mediaId);
        await _gate.WaitAsync();
        try
        {
            await _store.SetAsync(StorageKey, snapshot);
        }
        catch (Exception)
        {
            // Played marks are a convenience; the in-memory list still works for this session.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadAsync()
    {
        var user = _auth.UserId;
        List<Guid>? stored = null;
        if (user is not null)
        {
            try
            {
                stored = await _store.GetAsync<List<Guid>>(StorageKey);
            }
            catch (Exception)
            {
                stored = null;
            }
        }
        lock (_lock)
        {
            _order = (stored ?? []).Where(id => id != Guid.Empty).Distinct().TakeLast(MaxItems).ToList();
            _played = [.. _order];
            _loadedFor = user;
        }
        Raise(Guid.Empty);
    }

    private void Raise(Guid mediaId)
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action<Guid>)handler)(mediaId); }
            catch { /* isolated subscriber */ }
        }
    }
}
