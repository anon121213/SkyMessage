using Microsoft.Extensions.Logging;

namespace SkyMessage.Client.UI.Services;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record ToastMessage(Guid Id, string Text, ToastKind Kind, string? ActionText, Func<Task>? Action, TimeSpan Duration)
{
    public string Icon => Kind switch
    {
        ToastKind.Success => Kit.IconNames.CircleCheck,
        ToastKind.Warning => Kit.IconNames.TriangleAlert,
        ToastKind.Error => Kit.IconNames.CircleAlert,
        _ => Kit.IconNames.Info,
    };
}

/// <summary>
/// Short non-blocking messages at the bottom of the screen ("Link copied", "No connection"). Optional action
/// button ("Undo"). At most three are visible; identical texts are not stacked. Rendered by <c>ToastHost</c>. Scoped.
/// </summary>
public sealed class ToastService(ILocalizer localizer, ILogger<ToastService> logger) : IDisposable
{
    private const int MaxVisible = 3;
    private readonly List<ToastMessage> _items = [];
    private readonly CancellationTokenSource _lifetime = new();

    public IReadOnlyList<ToastMessage> Items
    {
        get
        {
            lock (_items) return [.. _items];
        }
    }

    /// <summary>May fire on any thread.</summary>
    public event Action? Changed;

    public Guid Show(string text, ToastKind kind = ToastKind.Info, string? actionText = null, Func<Task>? action = null, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Guid.Empty;
        var effective = duration ?? (action is not null ? TimeSpan.FromSeconds(5) : kind == ToastKind.Error ? TimeSpan.FromSeconds(4.5) : TimeSpan.FromSeconds(2.8));
        var toast = new ToastMessage(Guid.NewGuid(), text, kind, actionText, action, effective);
        lock (_items)
        {
            _items.RemoveAll(t => t.Text == text);
            _items.Add(toast);
            while (_items.Count > MaxVisible) _items.RemoveAt(0);
        }
        Raise();
        _ = ExpireAsync(toast);
        return toast.Id;
    }

    public Guid Success(string text) => Show(text, ToastKind.Success);

    public Guid Info(string text) => Show(text, ToastKind.Info);

    public Guid Warning(string text) => Show(text, ToastKind.Warning);

    /// <summary>Localized error text for an error code (<c>error.&lt;code&gt;</c>).</summary>
    public Guid Error(string? code) => Show(localizer.Error(code), ToastKind.Error);

    /// <summary>Localized error text for any exception (ApiException codes, network, timeouts).</summary>
    public Guid Error(Exception exception)
    {
        if (exception is OperationCanceledException and not TaskCanceledException) return Guid.Empty;
        return Show(localizer.Error(exception), ToastKind.Error);
    }

    public void Dismiss(Guid id)
    {
        bool removed;
        lock (_items) removed = _items.RemoveAll(t => t.Id == id) > 0;
        if (removed) Raise();
    }

    public async Task InvokeActionAsync(ToastMessage toast)
    {
        Dismiss(toast.Id);
        if (toast.Action is null) return;
        try
        {
            await toast.Action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Toast action failed");
            Error(ex);
        }
    }

    private async Task ExpireAsync(ToastMessage toast)
    {
        try
        {
            await Task.Delay(toast.Duration, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Dismiss(toast.Id);
    }

    private void Raise()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { logger.LogWarning(ex, "Toast subscriber failed"); }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
