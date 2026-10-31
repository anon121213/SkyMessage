using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;

namespace SkyMessage.Client.UI.Kit.Emoji;

/// <summary>
/// Recently used emoji of the signed-in account ("emoji.recent" in <see cref="IAccountLocalStore"/>, wiped on
/// sign-out). Cached in memory per account. Singleton, no JS.
/// </summary>
public sealed class EmojiRecents(IAccountLocalStore store, IAuthService auth, EmojiCatalog catalog)
{
    public const string StorageKey = "emoji.recent";
    public const int MaxItems = 32;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<string>? _cache;
    private Guid? _cacheUser;

    /// <summary>Raised after the list changed (any thread).</summary>
    public event Action? Changed;

    /// <summary>Most recent first; a friendly default set before the first use.</summary>
    public async Task<IReadOnlyList<string>> GetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return [.. await LoadAsync()];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(string emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return;
        await _gate.WaitAsync();
        try
        {
            var list = await LoadAsync();
            list.Remove(emoji);
            list.Insert(0, emoji);
            if (list.Count > MaxItems) list.RemoveRange(MaxItems, list.Count - MaxItems);
            try
            {
                await store.SetAsync(StorageKey, list.ToArray());
            }
            catch (Exception)
            {
                // Recents are a convenience; keep the in-memory copy when storage is unavailable.
            }
        }
        finally
        {
            _gate.Release();
        }
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch { /* isolated subscriber */ }
        }
    }

    private async Task<List<string>> LoadAsync()
    {
        var user = auth.UserId;
        if (_cache is not null && _cacheUser == user) return _cache;
        string[]? stored = null;
        try
        {
            if (user is not null) stored = await store.GetAsync<string[]>(StorageKey);
        }
        catch (Exception)
        {
            stored = null;
        }
        _cache = stored is { Length: > 0 } ? [.. stored.Where(e => !string.IsNullOrWhiteSpace(e)).Take(MaxItems)] : [.. catalog.DefaultRecent];
        _cacheUser = user;
        return _cache;
    }
}
