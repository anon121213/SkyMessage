using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// My folders and chat list settings (<see cref="IChatFolderStore"/>). Holds the newest server state it has seen (from
/// the cache, an answer, a GET or a ChatFoldersChanged event, compared by revision) and the changes still on their way;
/// the published state is the server state with those changes laid on top, so a newer state arriving meanwhile never
/// makes a pending change flicker away. Persisted per account as "folders".
/// </summary>
internal sealed class ChatFolderStore : IChatFolderStore, IAccountScoped
{
    private const string CacheName = "folders";
    private static readonly TimeSpan PersistDelay = TimeSpan.FromSeconds(1);

    private readonly ISkyApi _api;
    private readonly LocalCache _cache;
    private readonly AccountLifecycle _lifecycle;
    private readonly ILogger<ChatFolderStore> _logger;
    private readonly CoalescingAction _persist;
    private readonly object _lock = new();

    // Guarded by _lock.
    private ChatFoldersDto _server = new();
    // The held server state came from the cache only: the first state from the server replaces it whatever its revision
    // (the server's data may have been reset under an old cache).
    private bool _serverFromCache;
    private readonly List<Func<ChatFoldersDto, ChatFoldersDto>> _pending = [];
    private Task? _load;
    private bool _reloadAfterLoad;

    private volatile ChatFoldersDto _state = new();
    private volatile bool _isLoaded;
    private volatile bool _loadedFromServer;
    private volatile int _generation;

    public ChatFolderStore(ISkyApi api, IRealtimeService realtime, LocalCache cache, AccountLifecycle lifecycle, TimeProvider time,
        ILogger<ChatFolderStore> logger)
    {
        _api = api;
        _cache = cache;
        _lifecycle = lifecycle;
        _logger = logger;
        _persist = new CoalescingAction(PersistDelay, PersistAsync, time, logger);
        realtime.ChatFoldersChanged += state => Accept(state, _generation);
        realtime.Resynced += () =>
        {
            if (_loadedFromServer) LoadAsync(force: true).Forget(_logger);
        };
        lifecycle.Register(this);
    }

    public ChatFoldersDto State => _state;

    public IReadOnlyList<ChatFolderDto> Folders => _state.Folders;

    public ChatListSettingsDto Settings => _state.Settings;

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
                var state = await _api.GetChatFoldersAsync(_lifecycle.AccountToken).ConfigureAwait(false);
                if (generation != _generation) return;
                Accept(state, generation);
                _loadedFromServer = true;
            }
            catch (Exception ex) when (ex is ApiException or OperationCanceledException)
            {
                _logger.LogInformation("Folders refresh failed ({Error})", (ex as ApiException)?.Code ?? "cancelled");
            }

            lock (_lock)
            {
                if (!_reloadAfterLoad) return;
            }
        }
    }

    public ChatFolderDto? Get(Guid folderId)
    {
        foreach (var folder in _state.Folders)
            if (folder.Id == folderId) return folder;
        return null;
    }

    public Task SaveAsync(ChatFolderDto folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (folder.Id == Guid.Empty) throw new ArgumentException("A folder needs an id (Guid.CreateVersion7() for a new one).", nameof(folder));
        return MutateAsync(s => ChatFolderEdits.Save(s, folder), () => _api.SaveChatFolderAsync(folder.Id, SaveChatFolderRequest.From(folder)));
    }

    public Task DeleteAsync(Guid folderId) =>
        MutateAsync(s => ChatFolderEdits.Delete(s, folderId), () => _api.DeleteChatFolderAsync(folderId));

    public Task ReorderAsync(IReadOnlyList<Guid> folderIds) =>
        folderIds.Count == 0
            ? Task.CompletedTask
            : MutateAsync(s => ChatFolderEdits.Reorder(s, folderIds), () => _api.ReorderChatFoldersAsync(folderIds));

    public Task SetPinnedAsync(Guid folderId, Guid chatId, bool pinned) =>
        MutateAsync(s => ChatFolderEdits.SetPinned(s, folderId, chatId, pinned), () => _api.SetFolderPinAsync(folderId, chatId, pinned));

    public Task ReorderPinnedAsync(Guid folderId, IReadOnlyList<Guid> chatIdsTopFirst) =>
        chatIdsTopFirst.Count == 0
            ? Task.CompletedTask
            : MutateAsync(s => ChatFolderEdits.ReorderPinned(s, folderId, chatIdsTopFirst),
                () => _api.ReorderFolderPinsAsync(folderId, chatIdsTopFirst));

    public Task UpdateSettingsAsync(ChatListSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return MutateAsync(s => ChatFolderEdits.UpdateSettings(s, settings), () => _api.UpdateChatListSettingsAsync(settings));
    }

    public Task MarkReadAsync(Guid? folderId) => _api.MarkFolderReadAsync(folderId);

    async Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        var generation = _generation;
        var cached = await _cache.ReadAsync<ChatFoldersDto>(LocalCache.AccountKey(userId, CacheName)).ConfigureAwait(false);
        if (cached is null || generation != _generation) return;
        lock (_lock)
        {
            // A server state that arrived first is newer than anything cached.
            if (generation != _generation || _isLoaded) return;
            _server = Clean(cached);
            _serverFromCache = true;
            _isLoaded = true;
            Publish();
        }
        RaiseChanged();
    }

    Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        Interlocked.Increment(ref _generation);
        _persist.Cancel();
        _loadedFromServer = false;
        lock (_lock)
        {
            _server = new ChatFoldersDto();
            _serverFromCache = false;
            _pending.Clear();
            _isLoaded = false;
            Publish();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    /// <summary>A state from the server (answer, GET or event): kept when it is not older than the one held.</summary>
    private void Accept(ChatFoldersDto incoming, int generation)
    {
        bool changed;
        lock (_lock)
        {
            if (generation != _generation) return;
            changed = AcceptCore(incoming);
        }
        if (!changed) return;
        RaiseChanged();
        _persist.Request();
    }

    // Under _lock.
    private bool AcceptCore(ChatFoldersDto incoming)
    {
        // Same revision = the same state (the server numbers every change).
        if (_isLoaded && !_serverFromCache && incoming.Revision <= _server.Revision) return false;
        _server = Clean(incoming);
        _serverFromCache = false;
        _isLoaded = true;
        Publish();
        return true;
    }

    private async Task MutateAsync(Func<ChatFoldersDto, ChatFoldersDto> change, Func<Task<ChatFoldersDto>> send)
    {
        var generation = _generation;
        lock (_lock)
        {
            _pending.Add(change);
            Publish();
        }
        RaiseChanged();

        ChatFoldersDto result;
        try
        {
            result = await send().ConfigureAwait(false);
        }
        catch
        {
            lock (_lock)
            {
                if (generation == _generation && _pending.Remove(change)) Publish();
            }
            RaiseChanged();
            throw;
        }

        lock (_lock)
        {
            if (generation != _generation) return;
            _pending.Remove(change);
            // The answer is at least as new as anything held (the server applied this change on top of it); on a tie the
            // held state already is this one.
            if (!_isLoaded || _serverFromCache || result.Revision >= _server.Revision)
            {
                _server = Clean(result);
                _serverFromCache = false;
                _isLoaded = true;
            }
            Publish();
        }
        RaiseChanged();
        _persist.Request();
    }

    // Under _lock.
    private void Publish()
    {
        var state = _server;
        foreach (var change in _pending) state = change(state);
        _state = state;
    }

    /// <summary>A state as received may miss lists (older caches, a server that left them out): never null inside.</summary>
    private static ChatFoldersDto Clean(ChatFoldersDto state) => state with
    {
        Folders = (state.Folders ?? []).Where(f => f is not null && f.Id != Guid.Empty).Select(f => f with
        {
            Name = f.Name ?? "",
            IncludedChatIds = f.IncludedChatIds ?? [],
            ExcludedChatIds = f.ExcludedChatIds ?? [],
            PinnedChatIds = f.PinnedChatIds ?? [],
        }).ToList(),
        Settings = state.Settings ?? new ChatListSettingsDto(),
    };

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_lifecycle.ActiveUserId is not { } userId) return;
        ChatFoldersDto server;
        lock (_lock) server = _server;
        await _cache.WriteAsync(LocalCache.AccountKey(userId, CacheName), server, ct).ConfigureAwait(false);
    }

    private void RaiseChanged() => EventRaiser.Raise(Changed, _logger);
}
