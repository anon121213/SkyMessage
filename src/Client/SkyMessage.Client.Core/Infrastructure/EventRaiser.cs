using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>
/// Invokes every subscriber of an event in isolation: a throwing handler is logged and the remaining
/// handlers still run (Core contract for all services and stores).
/// </summary>
internal static class EventRaiser
{
    public static void Raise(Action? handler, ILogger logger, [CallerArgumentExpression(nameof(handler))] string? name = null)
    {
        if (handler is null) return;
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A subscriber of {Event} threw", name);
            }
        }
    }

    public static void Raise<T>(Action<T>? handler, T arg, ILogger logger, [CallerArgumentExpression(nameof(handler))] string? name = null)
    {
        if (handler is null) return;
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action<T>)subscriber)(arg);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A subscriber of {Event} threw", name);
            }
        }
    }

    public static void Raise<T1, T2>(Action<T1, T2>? handler, T1 arg1, T2 arg2, ILogger logger, [CallerArgumentExpression(nameof(handler))] string? name = null)
    {
        if (handler is null) return;
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action<T1, T2>)subscriber)(arg1, arg2);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A subscriber of {Event} threw", name);
            }
        }
    }

    /// <summary>Observes a fire-and-forget task so failures are logged instead of becoming unobserved exceptions.</summary>
    public static void Forget(this Task task, ILogger logger, [CallerArgumentExpression(nameof(task))] string? operation = null)
    {
        if (task.IsCompleted)
        {
            Observe(task, logger, operation);
            return;
        }

        task.ContinueWith(static (t, state) =>
            {
                var (log, op) = ((ILogger, string?))state!;
                Observe(t, log, op);
            }, (logger, operation), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void Observe(Task task, ILogger logger, string? operation)
    {
        if (task.IsFaulted)
        {
            var ex = task.Exception!.GetBaseException();
            if (ex is not OperationCanceledException)
                logger.LogWarning(ex, "Background operation {Operation} failed", operation);
        }
    }
}
