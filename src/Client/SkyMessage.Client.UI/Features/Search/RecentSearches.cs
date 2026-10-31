using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;

namespace SkyMessage.Client.UI.Features.Search;

public enum RecentSearchKind
{
    User,
    Chat,
}

/// <summary>A person or chat the user opened from search.</summary>
public sealed record RecentSearchEntry(RecentSearchKind Kind, Guid Id, string Title);

/// <summary>Recently opened search results of this account ("search.recent" in <see cref="IAccountLocalStore"/>). Singleton.</summary>
public sealed class RecentSearches(IAccountLocalStore store, IAuthService auth)
{
    public const string StorageKey = "search.recent";
    public const int MaxItems = 20;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<RecentSearchEntry>? _cache;
    private Guid? _cacheUser;

    public async Task<IReadOnlyList<RecentSearchEntry>> GetAsync()
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

    public Task AddAsync(RecentSearchEntry entry) => MutateAsync(list =>
    {
        list.RemoveAll(e => e.Kind == entry.Kind && e.Id == entry.Id);
        list.Insert(0, entry);
        if (list.Count > MaxItems) list.RemoveRange(MaxItems, list.Count - MaxItems);
    });

    public Task RemoveAsync(RecentSearchEntry entry) => MutateAsync(list => list.RemoveAll(e => e.Kind == entry.Kind && e.Id == entry.Id));

    public Task ClearAsync() => MutateAsync(list => list.Clear());

    private async Task MutateAsync(Action<List<RecentSearchEntry>> mutate)
    {
        await _gate.WaitAsync();
        try
        {
            var list = await LoadAsync();
            mutate(list);
            try
            {
                await store.SetAsync(StorageKey, list.ToArray());
            }
            catch (Exception)
            {
                // Best effort: recent searches are a convenience.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<RecentSearchEntry>> LoadAsync()
    {
        var user = auth.UserId;
        if (_cache is not null && _cacheUser == user) return _cache;
        RecentSearchEntry[]? stored = null;
        try
        {
            if (user is not null) stored = await store.GetAsync<RecentSearchEntry[]>(StorageKey);
        }
        catch (Exception)
        {
            stored = null;
        }
        _cache = stored is null ? [] : [.. stored.Where(e => e.Id != Guid.Empty).Take(MaxItems)];
        _cacheUser = user;
        return _cache;
    }
}
