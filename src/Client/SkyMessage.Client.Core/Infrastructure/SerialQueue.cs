namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>
/// One async mutation queue per store: operations run one at a time in arrival order. Operations must not
/// await network calls or enqueue into the same queue (that would deadlock); they transform state, may
/// await local cache writes, and return. WASM-safe (no blocking waits).
/// </summary>
internal sealed class SerialQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(Action action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            action();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<T> action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunAsync(Func<Task> action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
