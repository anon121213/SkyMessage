using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace SkyMessage.Client.UI.Services;

public enum DialogSize
{
    Small,
    Medium,
    Large,
}

/// <summary>How an overlay is presented: Auto = centered dialog on wide screens, bottom sheet on phones.</summary>
public enum OverlayPresentation
{
    Auto,
    Dialog,
    Sheet,
}

public sealed record DialogOptions
{
    public DialogSize Size { get; init; } = DialogSize.Small;
    /// <summary>Backdrop tap and the close button cancel the dialog (Back always cancels).</summary>
    public bool Dismissible { get; init; } = true;
    public bool ShowClose { get; init; } = true;
    public OverlayPresentation Presentation { get; init; } = OverlayPresentation.Dialog;
    /// <summary>Content draws its own padding (lists, pickers).</summary>
    public bool Flush { get; init; }
    public string? Class { get; init; }
}

/// <summary>An option for choices, tabs, segmented controls and selects.</summary>
public sealed record ChoiceOption<T>(T Value, string Label)
{
    public string? Icon { get; init; }
    public string? Description { get; init; }
    public bool Danger { get; init; }
    public bool Disabled { get; init; }
    public bool Selected { get; init; }
    public int? Badge { get; init; }
}

public sealed record ConfirmOptions
{
    public required string Title { get; init; }
    public string? Message { get; init; }
    public string? ConfirmText { get; init; }
    public string? CancelText { get; init; }
    public bool Danger { get; init; }
    /// <summary>Optional checkbox under the message ("Also delete for Anna").</summary>
    public string? CheckboxLabel { get; init; }
    public bool CheckboxDefault { get; init; }
}

public sealed record ConfirmResult(bool Confirmed, bool Checked);

public sealed record PromptOptions
{
    public required string Title { get; init; }
    public string? Message { get; init; }
    public string? Label { get; init; }
    public string? Placeholder { get; init; }
    public string? InitialValue { get; init; }
    public int MaxLength { get; init; } = 256;
    public string? ConfirmText { get; init; }
    public bool AllowEmpty { get; init; }
    /// <summary>HTML input type ("text", "url", "password"...).</summary>
    public string InputType { get; init; } = "text";
    public bool Multiline { get; init; }
    /// <summary>Returns an error text for invalid input, null when valid.</summary>
    public Func<string, string?>? Validate { get; init; }
}

/// <summary>
/// A dialog opened through <see cref="DialogService"/>. Cascaded to the dialog's content component as
/// <c>[CascadingParameter] public DialogReference Dialog { get; set; }</c>; call <see cref="Close"/> with a
/// result or <see cref="Cancel"/>.
/// </summary>
public sealed class DialogReference
{
    private readonly DialogService _owner;
    private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _done;

    internal DialogReference(DialogService owner, Type componentType, string? title, IDictionary<string, object?>? parameters, DialogOptions options)
    {
        _owner = owner;
        ComponentType = componentType;
        Title = title;
        Parameters = parameters ?? new Dictionary<string, object?>();
        Options = options;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public Type ComponentType { get; }
    public string? Title { get; set; }
    public IDictionary<string, object?> Parameters { get; }
    public DialogOptions Options { get; }
    public bool IsClosed => Volatile.Read(ref _done) == 1;
    public Task<object?> Result => _completion.Task;

    internal OverlayHandle? Overlay { get; set; }

    /// <summary>Raised when the dialog wants its frame re-rendered (title change).</summary>
    internal event Action? Updated;

    public void Close(object? result = null) => _ = _owner.CompleteAsync(this, result, fromBack: false);

    public void Cancel() => Close(null);

    /// <summary>Changes the title shown in the frame.</summary>
    public void SetTitle(string? title)
    {
        Title = title;
        Updated?.Invoke();
    }

    internal bool TryMarkDone() => Interlocked.Exchange(ref _done, 1) == 0;

    internal void SetResult(object? result) => _completion.TrySetResult(result);
}

/// <summary>
/// Programmatic dialogs (confirm, alert, prompt, choice sheet, custom components). Each open dialog owns a
/// history entry (Back cancels it). Results complete after the entry is removed, so callers may navigate
/// right away. Rendered by <c>DialogHost</c> in the shell. Scoped.
/// </summary>
public sealed class DialogService(NavigationService navigation, ILocalizer localizer, ILogger<DialogService> logger)
{
    private readonly List<DialogReference> _open = [];

    public IReadOnlyList<DialogReference> Open
    {
        get
        {
            lock (_open) return [.. _open];
        }
    }

    public event Action? Changed;

    /// <summary>
    /// Shows <typeparamref name="TComponent"/> inside a dialog frame. The task completes with the value passed
    /// to <see cref="DialogReference.Close"/>, or null when cancelled (Back, backdrop, close button).
    /// </summary>
    public async Task<object?> ShowAsync<TComponent>(string? title = null, IDictionary<string, object?>? parameters = null, DialogOptions? options = null)
        where TComponent : IComponent
    {
        var reference = new DialogReference(this, typeof(TComponent), title, parameters, options ?? new DialogOptions());
        lock (_open) _open.Add(reference);
        Raise();
        try
        {
            reference.Overlay = await navigation.OpenOverlayAsync(() =>
            {
                _ = CompleteAsync(reference, null, fromBack: true);
                return Task.CompletedTask;
            });
            if (reference.IsClosed) await reference.Overlay.CloseAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dialog history entry not created");
        }
        return await reference.Result;
    }

    public async Task<bool> ConfirmAsync(string title, string? message = null, string? confirmText = null, bool danger = false, string? cancelText = null)
    {
        var result = await ConfirmAsync(new ConfirmOptions
        {
            Title = title,
            Message = message,
            ConfirmText = confirmText,
            Danger = danger,
            CancelText = cancelText,
        });
        return result.Confirmed;
    }

    public async Task<ConfirmResult> ConfirmAsync(ConfirmOptions options)
    {
        var result = await ShowAsync<Kit.ConfirmDialog>(options.Title,
            new Dictionary<string, object?> { [nameof(Kit.ConfirmDialog.Options)] = options });
        return result as ConfirmResult ?? new ConfirmResult(false, false);
    }

    public async Task AlertAsync(string title, string? message = null, string? okText = null)
    {
        await ShowAsync<Kit.ConfirmDialog>(title, new Dictionary<string, object?>
        {
            [nameof(Kit.ConfirmDialog.Options)] = new ConfirmOptions { Title = title, Message = message, ConfirmText = okText ?? localizer["common.ok"] },
            [nameof(Kit.ConfirmDialog.AlertOnly)] = true,
        });
    }

    /// <summary>Text input dialog; null when cancelled.</summary>
    public async Task<string?> PromptAsync(PromptOptions options)
    {
        var result = await ShowAsync<Kit.PromptDialog>(options.Title,
            new Dictionary<string, object?> { [nameof(Kit.PromptDialog.Options)] = options });
        return result as string;
    }

    /// <summary>Action sheet: a list of options (bottom sheet on phones). Null when cancelled.</summary>
    public async Task<ChoiceOption<T>?> ChooseAsync<T>(string? title, IReadOnlyList<ChoiceOption<T>> options, string? message = null)
    {
        var result = await ShowAsync<Kit.ChoiceSheet<T>>(title, new Dictionary<string, object?>
        {
            [nameof(Kit.ChoiceSheet<T>.Options)] = options,
            [nameof(Kit.ChoiceSheet<T>.Message)] = message,
        }, new DialogOptions { Presentation = OverlayPresentation.Auto, Flush = true });
        return result as ChoiceOption<T>;
    }

    /// <summary>Cancels every open dialog (sign-out, call screen taking over).</summary>
    public async Task CloseAllAsync()
    {
        foreach (var reference in Open.AsEnumerable().Reverse())
            await CompleteAsync(reference, null, fromBack: false);
    }

    internal async Task CompleteAsync(DialogReference reference, object? result, bool fromBack)
    {
        if (!reference.TryMarkDone()) return;
        lock (_open) _open.Remove(reference);
        Raise();
        if (!fromBack && reference.Overlay is { IsOpen: true } overlay)
        {
            try
            {
                await overlay.CloseAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Dialog history entry not removed");
            }
        }
        reference.SetResult(result);
    }

    private void Raise()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { logger.LogWarning(ex, "Dialog subscriber failed"); }
        }
    }
}
