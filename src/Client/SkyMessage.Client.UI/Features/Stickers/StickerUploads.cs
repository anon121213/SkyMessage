using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI.Features.Stickers;

/// <summary>
/// The stickers on their way into packs of mine: pictures chosen in the pack editor upload one after another (in the order
/// they were chosen), here rather than in the editor, so leaving the editor does not lose them — they keep going, and
/// the editor shows them again (with their progress) when it opens. A failure says so in a toast and drops that
/// picture; signing out drops the rest. Scoped (one per app window), no JS.
/// </summary>
public sealed class StickerUploads : IDisposable
{
    private readonly IStickerStore _stickers;
    private readonly IAuthService _auth;
    private readonly ToastService _toasts;
    private readonly HapticFeedback _haptics;
    private readonly ILogger<StickerUploads> _logger;
    private readonly List<PendingSticker> _queue = [];
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
    }

    /// <summary>A sticker was added, made progress or failed (any thread).</summary>
    public event Action? Changed;

    /// <summary>The pack's stickers still on their way, in order.</summary>
    public IReadOnlyList<PendingSticker> For(Guid packId)
    {
        lock (_lock) return _queue.Where(p => p.PackId == packId).ToList();
    }

    public void Add(Guid packId, PickedFile file, IReadOnlyList<string> emoji)
    {
        lock (_lock)
        {
            _queue.Add(new PendingSticker { PackId = packId, File = file, Emoji = emoji });
            _worker ??= RunAsync(_cts.Token);
        }
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
            try
            {
                await _stickers.AddStickerAsync(item.PackId, item.File, item.Emoji, progress, ct);
                _haptics.Selection();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
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

    private void OnAuthChanged()
    {
        if (_auth.State != AuthState.SignedOut) return;
        CancellationTokenSource old;
        lock (_lock)
        {
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
        _cts.Cancel();
        _cts.Dispose();
    }
}
