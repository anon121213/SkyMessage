using Microsoft.Extensions.Logging;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Marshals work from Core events (which fire on any thread) onto the renderer's dispatcher, for scoped
/// services that are not components. Routes.razor attaches its own InvokeAsync on startup. Scoped.
/// </summary>
public sealed class UiDispatcher(ILogger<UiDispatcher> logger)
{
    private Func<Func<Task>, Task>? _invoke;

    internal void Attach(Func<Func<Task>, Task> invoke) => _invoke = invoke;

    public bool IsAttached => _invoke is not null;

    public Task InvokeAsync(Func<Task> work) => _invoke is { } invoke ? invoke(work) : work();

    public Task InvokeAsync(Action work) => InvokeAsync(() =>
    {
        work();
        return Task.CompletedTask;
    });

    /// <summary>Runs on the dispatcher without awaiting; failures are logged, never thrown.</summary>
    public void Post(Func<Task> work) => _ = RunSafeAsync(work);

    public void Post(Action work) => _ = RunSafeAsync(() =>
    {
        work();
        return Task.CompletedTask;
    });

    private async Task RunSafeAsync(Func<Task> work)
    {
        try
        {
            await InvokeAsync(work);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UI work failed");
        }
    }
}

public static class TaskExtensions
{
    /// <summary>Fire-and-forget with logging (never an unobserved exception).</summary>
    public static async void Forget(this Task task, ILogger? logger = null)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Background UI task failed");
        }
    }
}
