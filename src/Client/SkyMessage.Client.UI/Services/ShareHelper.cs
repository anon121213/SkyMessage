using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.Client.UI.Services;

/// <summary>Copy/share with user feedback (toast) and fallbacks. Scoped.</summary>
public sealed class ShareHelper(IShareService share, AppJs js, ToastService toasts, ILocalizer localizer, HapticFeedback haptics, ILogger<ShareHelper> logger)
{
    /// <summary>Copies text to the clipboard and confirms with a toast (default "Copied").</summary>
    public async Task<bool> CopyAsync(string text, string? toastText = null)
    {
        var ok = false;
        try
        {
            ok = await share.CopyToClipboardAsync(text);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Native clipboard failed");
        }
        if (!ok) ok = await js.CopyTextAsync(text);
        if (ok)
        {
            haptics.Success();
            toasts.Success(toastText ?? localizer["common.copied"]);
        }
        else
        {
            toasts.Error(localizer["common.copyFailed"]);
        }
        return ok;
    }

    /// <summary>Opens the system share sheet; copies the text instead when sharing is unavailable.</summary>
    public async Task ShareAsync(string text, string? title = null)
    {
        try
        {
            await share.ShareTextAsync(text, title);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Share sheet unavailable, copying instead");
            await CopyAsync(text);
        }
    }
}
