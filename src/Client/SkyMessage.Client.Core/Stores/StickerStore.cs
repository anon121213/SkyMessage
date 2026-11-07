using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// My stickers and GIFs (<see cref="IStickerStore"/>), the chat folder store's pattern: the newest server state seen (the
/// cache, an answer, a GET or a StickersChanged event, compared by revision) with the changes still on their way laid on
/// top, so a newer state arriving meanwhile never makes a pending change flicker away. The stickers of installed packs
/// are kept by version next to it; a state naming a newer version (or a pack not held yet) fetches that pack again.
/// Persisted per account as "stickers" and "sticker-packs".
/// </summary>
internal sealed class StickerStore : IStickerStore, IAccountScoped
{
    private const string CacheName = "stickers";
    private const string PacksCacheName = "sticker-packs";
    private static readonly TimeSpan PersistDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly ISkyApi _api;
    private readonly IMediaUploader _uploader;
    private readonly LocalCache _cache;
    private readonly AccountLifecycle _lifecycle;
    private readonly ILogger<StickerStore> _logger;
    private readonly TimeProvider _time;
    private readonly CoalescingAction _persist;
    private readonly object _lock = new();

    // Guarded by _lock.
    private StickerStateDto _server = new();
    private bool _serverFromCache;
    private readonly List<Func<StickerStateDto, StickerStateDto>> _pending = [];
    private readonly Dictionary<Guid, StickerPackDto> _packs = [];
    private readonly HashSet<Guid> _fetching = [];
    private Task? _load;
    private bool _reloadAfterLoad;
    private bool _retryIfFailed;

    private volatile StickerStateDto _state = new();
    private volatile bool _isLoaded;
    private volatile bool _loadedFromServer;
    private volatile int _generation;

    public StickerStore(ISkyApi api, IRealtimeService realtime, IMediaUploader uploader, LocalCache cache, AccountLifecycle lifecycle,
        TimeProvider time, ILogger<StickerStore> logger)
    {
        _api = api;
        _uploader = uploader;
        _cache = cache;
        _lifecycle = lifecycle;
        _logger = logger;
        _time = time;
        _persist = new CoalescingAction(PersistDelay, PersistAsync, time, logger);
        realtime.StickersChanged += state => Accept(state, _generation);
        realtime.Resynced += OnResynced;
        lifecycle.Register(this);
    }

    public StickerStateDto State => _state;

    public bool IsLoaded => _isLoaded;

    public event Action? Changed;

    private void OnResynced()
    {
        lock (_lock)
        {
            if (_load is { IsCompleted: false }) _retryIfFailed = true;
        }
        LoadAsync(force: _loadedFromServer).Forget(_logger);
    }

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
            var failed = false;
            try
            {
                var state = await _api.GetStickerStateAsync(_lifecycle.AccountToken).ConfigureAwait(false);
                if (generation != _generation) return;
                Accept(state, generation, loaded: true);
                _loadedFromServer = true;
            }
            catch (Exception ex) when (ex is ApiException or OperationCanceledException)
            {
                _logger.LogInformation("Stickers refresh failed ({Error})", (ex as ApiException)?.Code ?? "cancelled");
                failed = true;
            }

            bool again;
            bool retry;
            lock (_lock)
            {
                again = _reloadAfterLoad;
                retry = failed && _retryIfFailed && !again;
                _retryIfFailed = false;
            }
            if (generation != _generation) return;
            if (retry)
            {
                await Task.Delay(RetryDelay, _time).ConfigureAwait(false);
                if (generation != _generation) return;
                continue;
            }
            if (!again) return;
        }
    }

    // ---- Packs ----

    public StickerPackDto? GetPack(Guid packId)
    {
        lock (_lock) return _packs.GetValueOrDefault(packId);
    }

    public async Task<StickerPackDto> LoadPackAsync(Guid packId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && GetPack(packId) is { } held) return held;
        var generation = _generation;
        try
        {
            var pack = await _api.GetStickerPackAsync(packId, cancellationToken).ConfigureAwait(false);
            KeepPack(pack, generation);
            return pack;
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.StickerPackNotFound)
        {
            ForgetPack(packId, generation);
            throw;
        }
    }

    public async Task<StickerPackDto> LoadPackBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var generation = _generation;
        var pack = await _api.GetStickerPackBySlugAsync(slug, cancellationToken).ConfigureAwait(false);
        KeepPack(pack, generation);
        return pack;
    }

    /// <summary>Keeps the newest copy of a pack (by version); persisted when installed.</summary>
    private void KeepPack(StickerPackDto pack, int generation)
    {
        bool changed;
        lock (_lock)
        {
            if (generation != _generation) return;
            changed = !_packs.TryGetValue(pack.Id, out var held) || held.Version <= pack.Version;
            if (changed) _packs[pack.Id] = Clean(pack);
        }
        if (!changed) return;
        RaiseChanged();
        _persist.Request();
    }

    private void ForgetPack(Guid packId, int generation)
    {
        bool removed;
        lock (_lock)
        {
            if (generation != _generation) return;
            removed = _packs.Remove(packId);
        }
        if (!removed) return;
        RaiseChanged();
        _persist.Request();
    }

    /// <summary>Fetches the installed packs whose stickers are not held at the version the state names (one at a time).</summary>
    private void RefreshPacks(int generation)
    {
        List<Guid> stale;
        lock (_lock)
        {
            if (generation != _generation) return;
            stale = _state.Installed
                .Where(r => (!_packs.TryGetValue(r.Id, out var held) || held.Version < r.Version) && _fetching.Add(r.Id))
                .Select(r => r.Id)
                .ToList();
        }
        if (stale.Count == 0) return;
        FetchAsync().Forget(_logger);

        async Task FetchAsync()
        {
            foreach (var id in stale)
            {
                try
                {
                    var pack = await _api.GetStickerPackAsync(id, _lifecycle.AccountToken).ConfigureAwait(false);
                    KeepPack(pack, generation);
                }
                catch (ApiException ex) when (ex.Code == ErrorCodes.StickerPackNotFound)
                {
                    ForgetPack(id, generation);
                }
                catch (Exception ex) when (ex is ApiException or OperationCanceledException)
                {
                    _logger.LogInformation("Sticker pack refresh failed ({Error})", (ex as ApiException)?.Code ?? "cancelled");
                }
                finally
                {
                    lock (_lock) _fetching.Remove(id);
                }
            }
        }
    }

    // ---- Lookups ----

    public bool IsInstalled(Guid packId) => _state.Installed.Any(p => p.Id == packId);

    public bool IsFavorite(Guid stickerId) => _state.Favorites.Any(s => s.Id == stickerId);

    public bool IsSavedGif(Guid mediaId) => _state.Gifs.Any(g => g.Media.Id == mediaId);

    public IReadOnlyList<StickerDto> Suggest(string emoji, int max = 24)
    {
        if (string.IsNullOrWhiteSpace(emoji) || max <= 0) return [];
        var result = new List<StickerDto>();
        var seen = new HashSet<Guid>();
        lock (_lock)
        {
            foreach (var installed in _state.Installed)
            {
                if (!_packs.TryGetValue(installed.Id, out var pack)) continue;
                foreach (var sticker in pack.Stickers)
                {
                    if (!StickerRules.Matches(sticker, emoji) || !seen.Add(sticker.Id)) continue;
                    result.Add(sticker);
                    if (result.Count >= max) return result;
                }
            }
        }
        return result;
    }

    // ---- My state ----

    public Task InstallAsync(StickerPackDto pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        KeepPack(pack, _generation);
        return MutateAsync(s => StickerEdits.Install(s, pack), () => _api.InstallStickerPackAsync(pack.Id));
    }

    public Task UninstallAsync(Guid packId) =>
        MutateAsync(s => StickerEdits.Uninstall(s, packId), () => _api.UninstallStickerPackAsync(packId));

    public Task ReorderAsync(IReadOnlyList<Guid> packIds) =>
        packIds.Count == 0
            ? Task.CompletedTask
            : MutateAsync(s => StickerEdits.Reorder(s, packIds), () => _api.ReorderStickerPacksAsync(packIds));

    public Task SetFavoriteAsync(StickerDto sticker, bool favorite)
    {
        ArgumentNullException.ThrowIfNull(sticker);
        return MutateAsync(s => StickerEdits.SetFavorite(s, sticker, favorite), () => _api.SetFavoriteStickerAsync(sticker.Id, favorite));
    }

    public Task SaveGifAsync(Guid chatId, Guid messageId, MediaDto gif)
    {
        ArgumentNullException.ThrowIfNull(gif);
        return MutateAsync(s => StickerEdits.SaveGif(s, gif, _time.GetUtcNow()), () => _api.SaveGifAsync(chatId, messageId));
    }

    public Task RemoveGifAsync(Guid mediaId) =>
        MutateAsync(s => StickerEdits.RemoveGif(s, mediaId), () => _api.RemoveGifAsync(mediaId));

    // ---- Packs I make ----

    public async Task<StickerPackDto> CreatePackAsync(string title)
    {
        var generation = _generation;
        var pack = await _api.CreateStickerPackAsync(new CreateStickerPackRequest { Id = Guid.CreateVersion7(), Title = title })
            .ConfigureAwait(false);
        KeepPack(pack, generation);
        // My state (the pack installed and among mine) arrives as StickersChanged; a new revision is asked for meanwhile.
        LoadAsync(force: true).Forget(_logger);
        return pack;
    }

    public Task<StickerPackDto> RenamePackAsync(Guid packId, string title) =>
        EditPackAsync(packId, () => _api.RenameStickerPackAsync(packId, title));

    public async Task<StickerPackDto> AddStickerAsync(Guid packId, PickedFile image, IReadOnlyList<string> emoji, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var size = image.Size;
        var open = image.OpenReadAsync;
        if (size <= 0)
        {
            // Some pickers do not know the size: read it (a sticker's source is at most 5 MB, a little more is refused).
            var bytes = await ReadAtMostAsync(image, Limits.StickerSourceMaxBytes + 1, cancellationToken).ConfigureAwait(false);
            size = bytes.Length;
            open = _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
        if (size > Limits.StickerSourceMaxBytes) throw new ApiException(400, ErrorCodes.StickerImageInvalid, "The picture is larger than 5 MB.");
        // The original bytes (a file): the server makes the WebP itself and keeps the transparency.
        var source = await _uploader.UploadAsync(new MediaUploadRequest
        {
            Kind = MediaKind.File,
            FileName = string.IsNullOrWhiteSpace(image.FileName) ? "sticker.png" : image.FileName,
            ContentType = string.IsNullOrWhiteSpace(image.ContentType) ? "image/png" : image.ContentType,
            Size = size,
            OpenReadAsync = open,
        }, progress, cancellationToken).ConfigureAwait(false);
        return await EditPackAsync(packId, () => _api.AddStickerAsync(packId, new AddStickerRequest
        {
            Id = Guid.CreateVersion7(),
            MediaId = source.Id,
            Emoji = emoji,
        }, cancellationToken)).ConfigureAwait(false);
    }

    public Task<StickerPackDto> SetStickerEmojiAsync(Guid packId, Guid stickerId, IReadOnlyList<string> emoji) =>
        EditPackAsync(packId, () => _api.UpdateStickerAsync(packId, stickerId, emoji));

    public Task<StickerPackDto> DeleteStickerAsync(Guid packId, Guid stickerId) =>
        EditPackAsync(packId, () => _api.DeleteStickerAsync(packId, stickerId));

    public Task<StickerPackDto> ReorderStickersAsync(Guid packId, IReadOnlyList<Guid> stickerIds) =>
        EditPackAsync(packId, () => _api.ReorderStickersAsync(packId, stickerIds));

    public async Task DeletePackAsync(Guid packId)
    {
        var generation = _generation;
        await _api.DeleteStickerPackAsync(packId).ConfigureAwait(false);
        Gone(packId, generation);
    }

    /// <summary>
    /// A pack deleted (here, or elsewhere as an edit found out) is not in my lists any more: shown at once, confirmed by
    /// the next state.
    /// </summary>
    private void Gone(Guid packId, int generation)
    {
        ForgetPack(packId, generation);
        lock (_lock)
        {
            if (generation != _generation) return;
            _server = StickerEdits.ForgetPack(_server, packId);
            Publish();
        }
        RaiseChanged();
        LoadAsync(force: true).Forget(_logger);
    }

    private static async Task<byte[]> ReadAtMostAsync(PickedFile file, long limit, CancellationToken ct)
    {
        await using var stream = await file.OpenReadAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length >= limit) break;
        }
        return buffer.ToArray();
    }

    private async Task<StickerPackDto> EditPackAsync(Guid packId, Func<Task<StickerPackDto>> edit)
    {
        var generation = _generation;
        StickerPackDto pack;
        try
        {
            pack = await edit().ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.StickerPackNotFound)
        {
            // Deleted on another of my devices before this one heard (its StickersChanged is still on the way).
            Gone(packId, generation);
            throw;
        }
        KeepPack(pack, generation);
        return pack;
    }

    // ---- Account ----

    async Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        var generation = _generation;
        var cached = await _cache.ReadAsync<StickerStateDto>(LocalCache.AccountKey(userId, CacheName)).ConfigureAwait(false);
        var packs = await _cache.ReadAsync<List<StickerPackDto>>(LocalCache.AccountKey(userId, PacksCacheName)).ConfigureAwait(false);
        if (generation != _generation) return;
        lock (_lock)
        {
            if (generation != _generation) return;
            foreach (var pack in packs ?? [])
                if (pack is not null && pack.Id != Guid.Empty) _packs.TryAdd(pack.Id, Clean(pack));
            // A server state that arrived first is newer than anything cached.
            if (cached is not null && !_isLoaded)
            {
                _server = Clean(cached);
                _serverFromCache = true;
                _isLoaded = true;
            }
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
            _server = new StickerStateDto();
            _serverFromCache = false;
            _pending.Clear();
            _packs.Clear();
            _fetching.Clear();
            _isLoaded = false;
            Publish();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    // ---- State ----

    /// <summary>A state from the server (answer, GET or event): kept when it is not older than the one held.</summary>
    /// <param name="loaded">Asked for (a GET): at the same revision it still brings what the server decides as it builds
    /// a state (which saved GIFs I can still see).</param>
    private void Accept(StickerStateDto incoming, int generation, bool loaded = false)
    {
        bool changed;
        lock (_lock)
        {
            if (generation != _generation) return;
            changed = AcceptCore(incoming, loaded);
        }
        if (!changed) return;
        RaiseChanged();
        _persist.Request();
        RefreshPacks(generation);
    }

    // Under _lock.
    private bool AcceptCore(StickerStateDto incoming, bool loaded)
    {
        if (_isLoaded && !_serverFromCache)
        {
            // Same revision = the same changes (the server numbers every one) — but whether I can still see a saved GIF
            // follows other people's actions (a message deleted for everyone): a load at the same revision brings it.
            if (incoming.Revision < _server.Revision) return false;
            if (incoming.Revision == _server.Revision && (!loaded || SameGifs(incoming.Gifs, _server.Gifs))) return false;
        }
        _server = Clean(incoming);
        _serverFromCache = false;
        _isLoaded = true;
        Publish();
        return true;
    }

    private async Task MutateAsync(Func<StickerStateDto, StickerStateDto> change, Func<Task<StickerStateDto>> send)
    {
        var generation = _generation;
        lock (_lock)
        {
            _pending.Add(change);
            Publish();
        }
        RaiseChanged();

        StickerStateDto result;
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
            // The answer is at least as new as anything held (the server applied this change on top of it).
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
        RefreshPacks(generation);
    }

    // Under _lock.
    private void Publish()
    {
        var state = _server;
        foreach (var change in _pending) state = change(state);
        _state = state;
    }

    private static bool SameGifs(IReadOnlyList<SavedGifDto>? a, IReadOnlyList<SavedGifDto> b) =>
        a is not null && a.Count == b.Count && a.Zip(b).All(p => p.First?.Media?.Id == p.Second.Media.Id && p.First.Unavailable == p.Second.Unavailable);

    /// <summary>A state as received may miss lists (older caches): never null inside.</summary>
    private static StickerStateDto Clean(StickerStateDto state) => state with
    {
        Installed = (state.Installed ?? []).Where(p => p is not null && p.Id != Guid.Empty).ToList(),
        Mine = (state.Mine ?? []).Where(p => p is not null && p.Id != Guid.Empty).ToList(),
        Favorites = (state.Favorites ?? []).Where(Valid).Select(CleanSticker).ToList(),
        Recent = (state.Recent ?? []).Where(Valid).Select(CleanSticker).ToList(),
        Gifs = (state.Gifs ?? []).Where(g => g?.Media is not null && g.Media.Id != Guid.Empty).ToList(),
    };

    private static StickerPackDto Clean(StickerPackDto pack) => pack with
    {
        Title = pack.Title ?? "",
        Stickers = (pack.Stickers ?? []).Where(Valid).Select(CleanSticker).ToList(),
    };

    private static bool Valid(StickerDto? sticker) => sticker?.Media is not null && sticker.Id != Guid.Empty;

    private static StickerDto CleanSticker(StickerDto sticker) => sticker with { Emoji = sticker.Emoji ?? [] };

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_lifecycle.ActiveUserId is not { } userId) return;
        StickerStateDto server;
        List<StickerPackDto> packs;
        lock (_lock)
        {
            server = _server;
            var installed = _server.Installed.Select(p => p.Id).ToHashSet();
            packs = _packs.Values.Where(p => installed.Contains(p.Id)).ToList();
        }
        await _cache.WriteAsync(LocalCache.AccountKey(userId, CacheName), server, ct).ConfigureAwait(false);
        await _cache.WriteAsync(LocalCache.AccountKey(userId, PacksCacheName), packs, ct).ConfigureAwait(false);
    }

    private void RaiseChanged() => EventRaiser.Raise(Changed, _logger);
}

/// <summary>How my changes look before the server answers (the same results the server's rules give).</summary>
internal static class StickerEdits
{
    public static StickerStateDto Install(StickerStateDto state, StickerPackDto pack)
    {
        if (state.Installed.Any(p => p.Id == pack.Id)) return state;
        return state with { Installed = [RefOf(pack), .. state.Installed] };
    }

    public static StickerStateDto Uninstall(StickerStateDto state, Guid packId) =>
        state.Installed.Any(p => p.Id == packId) ? state with { Installed = state.Installed.Where(p => p.Id != packId).ToList() } : state;

    public static StickerStateDto Reorder(StickerStateDto state, IReadOnlyList<Guid> packIds)
    {
        var byId = state.Installed.ToDictionary(p => p.Id);
        var arranged = StickerRules.Arrange(state.Installed.Select(p => p.Id).ToList(), packIds);
        return state with { Installed = arranged.Select(id => byId[id]).ToList() };
    }

    public static StickerStateDto SetFavorite(StickerStateDto state, StickerDto sticker, bool favorite)
    {
        var present = state.Favorites.Any(s => s.Id == sticker.Id);
        if (favorite == present) return state;
        return state with
        {
            Favorites = favorite
                ? new[] { sticker }.Concat(state.Favorites).Take(Limits.MaxFavoriteStickers).ToList()
                : state.Favorites.Where(s => s.Id != sticker.Id).ToList(),
        };
    }

    public static StickerStateDto SaveGif(StickerStateDto state, MediaDto gif, DateTimeOffset now) => state with
    {
        Gifs = new[] { new SavedGifDto { Media = gif with { MiniThumbnail = null }, SavedAt = now } }
            .Concat(state.Gifs.Where(g => g.Media.Id != gif.Id))
            .Take(Limits.MaxSavedGifs)
            .ToList(),
    };

    public static StickerStateDto RemoveGif(StickerStateDto state, Guid mediaId) =>
        state.Gifs.Any(g => g.Media.Id == mediaId) ? state with { Gifs = state.Gifs.Where(g => g.Media.Id != mediaId).ToList() } : state;

    /// <summary>A deleted pack leaves every list (its stickers too).</summary>
    public static StickerStateDto ForgetPack(StickerStateDto state, Guid packId) => state with
    {
        Installed = state.Installed.Where(p => p.Id != packId).ToList(),
        Mine = state.Mine.Where(p => p.Id != packId).ToList(),
        Favorites = state.Favorites.Where(s => s.PackId != packId).ToList(),
        Recent = state.Recent.Where(s => s.PackId != packId).ToList(),
    };

    public static StickerPackRefDto RefOf(StickerPackDto pack) => new()
    {
        Id = pack.Id,
        Slug = pack.Slug,
        Title = pack.Title,
        CreatorId = pack.CreatorId,
        Version = pack.Version,
        StickerCount = pack.Stickers.Count,
        Cover = pack.Stickers.FirstOrDefault(),
    };
}
