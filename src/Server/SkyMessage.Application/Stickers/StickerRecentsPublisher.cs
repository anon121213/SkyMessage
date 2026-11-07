using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SkyMessage.Application.Stickers;

/// <summary>
/// Recent stickers change with every sticker sent, and each change would send the whole state (packs, favorites, up to
/// 200 saved GIFs) to every device of the sender. So their StickersChanged waits <see cref="Delay"/> and goes once for a
/// burst of sends, built when it goes out (the newest state; the revision moved with each send at once). Every other
/// change still publishes at once. In memory: a restart loses a pending one, which the apps' next load makes up for.
/// </summary>
internal sealed class StickerRecentsPublisher(IServiceScopeFactory scopes, TimeProvider time, ILogger<StickerRecentsPublisher> logger)
{
    internal static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    /// <summary>Sends the user's state soon (one already waiting covers this change too).</summary>
    public void Schedule(Guid userId)
    {
        if (_pending.TryAdd(userId, 0)) _ = RunAsync(userId);
    }

    private async Task RunAsync(Guid userId)
    {
        try
        {
            await Task.Delay(Delay, time);
        }
        finally
        {
            // Taken off before the state is built: a send committed from now on schedules a publish of its own.
            _pending.TryRemove(userId, out _);
        }
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<StickerStates>().PublishAsync([userId]);
        }
        catch (ObjectDisposedException)
        {
            // The server is stopping.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recent stickers of user {UserId} could not be sent to their devices", userId);
        }
    }
}
