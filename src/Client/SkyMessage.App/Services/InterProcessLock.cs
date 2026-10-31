using System.Collections.Concurrent;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Services;

/// <summary>The native app runs in a single process, so a named in-process async lock is enough.</summary>
public sealed class InterProcessLock : IInterProcessLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
