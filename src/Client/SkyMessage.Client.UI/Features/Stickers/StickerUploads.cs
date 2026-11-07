using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Stickers;

/// <summary>
/// The stickers on their way into packs of mine: pictures chosen in the pack editor upload one after another (in the order
/// they were chosen), here rather than in the editor, so leaving the editor does not lose them — they keep going, and
/// the editor shows them again (with their progress) when it opens. A failure says so in a toast and drops that
/// picture. A pack that goes — deleted in its editor (<see cref="Cancel"/>), on another of my devices (it leaves my
/// packs in <see cref="IStickerStore.State"/>) or found gone by the server — takes its pictures along, quietly, and the
/// one on its way stops where it is. Signing out drops the rest. Scoped (one per app window), no JS.
/// </summary>
public sealed class StickerUploads : IDisposable
{
    private readonly IStickerStore _stickers;
    private readonly IAuthService _auth;
    private readonly ToastService _toasts;
    private readonly HapticFeedback _haptics;
    private readonly ILogger<StickerUploads> _logger;
    private readonly List<PendingSticker> _queue = [];
    // Packs with pictures waiting that my state has shown among mine: when they leave that list, they were deleted (a
    // pack made a moment ago may not be listed yet — it is not taken for a deleted one).
    private readonly HashSet<Guid> _listed = [];
    private readonly object _lock = new();
    private CancellationTokenSource _cts = new();
    private Task? _worker;

    public StickerUploads(IStickerStore stickers, IAuthService auth, ToastService toasts, HapticFeedback haptics, ILogger<StickerUploads> logger)
    {
        _stickers = stickers;
        _auth = auth;
        _toasts = toasts;
        _haptics = haptics;
        _logger = logger;
        _auth.Changed += OnAuthChanged;
        _stickers.Changed += OnStickersChanged;
    }

    /// <summary>A picture waiting for (or in) its upload.</summary>
    public sealed class PendingSticker
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required Guid PackId { get; init; }
        public required PickedFile File { get; init; }
        public required IReadOnlyList<string> Emoji { get; init; }
        /// <summary>0..1 (1 once the server has it).</summary>
        public double Progress { get; internal set; }
        /// <summary>Cancelled when its pack goes: its upload stops where it is.</summary>
        internal CancellationTokenSource Stop { get; } = new();
    }

    /// <summary>A sticker was added, made progress, failed or was dropped with its pack (any thread).</summary>
    public event Action? Changed;

    /// <summary>The pack's stickers still on their way, in order.</summary>
    public IReadOnlyList<PendingSticker> For(Guid packId)
    {
        lock (_lock) return _queue.Where(p => p.PackId == packId).ToList();
    }

    public void Add(Guid packId, PickedFile file, IReadOnlyList<string> emoji)
    {
        var listed = IsListed(packId);
        lock (_lock)
        {
            if (listed) _listed.Add(packId);
            _queue.Add(new PendingSticker { PackId = packId, File = file, Emoji = emoji });
            _worker ??= RunAsync(_cts.Token);
        }
        Raise();
    }

    /// <summary>
    /// The pack is being deleted (or is gone): its pictures still waiting leave the queue and the one on its way stops
    /// (no more data spent on it, no error for each). Quiet — whoever deletes a pack knows it.
    /// </summary>
    public void Cancel(Guid packId)
    {
        List<PendingSticker> dropped;
        lock (_lock)
        {
            _listed.Remove(packId);
            dropped = _queue.Where(p => p.PackId == packId).ToList();
            if (dropped.Count == 0) return;
            _queue.RemoveAll(p => p.PackId == packId);
        }
        foreach (var item in dropped) item.Stop.Cancel();
        Raise();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (true)
        {
            PendingSticker? next;
            lock (_lock)
            {
                next = ct.IsCancellationRequested ? null : _queue.FirstOrDefault();
                if (next is null)
                {
                    _worker = null;
                    return;
                }
            }
            var item = next;
            var progress = new Progress<double>(value =>
            {
                item.Progress = value;
                Raise();
            });
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, item.Stop.Token);
            try
            {
                await _stickers.AddStickerAsync(item.PackId, item.File, item.Emoji, progress, stop.Token);
                _haptics.Selection();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Signed out: the queue went with the account.
                return;
            }
            catch (Exception) when (item.Stop.IsCancellationRequested)
            {
                // Its pack went (Cancel took it and the rest of its pictures out of the queue): nothing to tell.
            }
            catch (ApiException ex) when (ex.Code == ErrorCodes.StickerPackNotFound)
            {
                // Deleted on another device before this one heard: the rest of its pictures go too, without a toast each
                // (the store has taken the pack out of my lists, so its editor says it is gone).
                _logger.LogInformation("Sticker pack {PackId} is gone; its waiting pictures are dropped", item.PackId);
                Cancel(item.PackId);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "A sticker could not be added to pack {PackId}", item.PackId);
                _toasts.Error(ex);
            }
            lock (_lock) _queue.Remove(item);
            Raise();
        }
    }

    private bool IsListed(Guid packId) => _stickers.IsLoaded && _stickers.State.Mine.Any(p => p.Id == packId);

    /// <summary>
    /// A pack of mine with pictures waiting that has left my packs was deleted (here or on another device).
    /// </summary>
    private void OnStickersChanged()
    {
        if (!_stickers.IsLoaded) return;
        var mine = _stickers.State.Mine;
        List<Guid>? gone = null;
        lock (_lock)
        {
            if (_queue.Count == 0)
            {
                _listed.Clear();
                return;
            }
            var waiting = _queue.Select(p => p.PackId).ToHashSet();
            _listed.IntersectWith(waiting);
            foreach (var packId in waiting)
            {
                if (mine.Any(p => p.Id == packId)) _listed.Add(packId);
                else if (_listed.Contains(packId)) (gone ??= []).Add(packId);
            }
        }
        foreach (var packId in gone ?? [])
        {
            _logger.LogInformation("Sticker pack {PackId} left my packs; its waiting pictures are dropped", packId);
            Cancel(packId);
        }
    }

    private void OnAuthChanged()
    {
        if (_auth.State != AuthState.SignedOut) return;
        CancellationTokenSource old;
        lock (_lock)
        {
            _listed.Clear();
            if (_queue.Count == 0 && _worker is null) return;
            _queue.Clear();
            old = _cts;
            _cts = new CancellationTokenSource();
            _worker = null;
        }
        old.Cancel();
        old.Dispose();
        Raise();
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A sticker upload listener failed");
        }
    }

    public void Dispose()
    {
        _auth.Changed -= OnAuthChanged;
        _stickers.Changed -= OnStickersChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}
