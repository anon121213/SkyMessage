using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Services;

public sealed class ShareService(NativeStrings strings, ILogger<ShareService> logger) : IShareService
{
    public async Task ShareTextAsync(string text, string? title = null)
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareTextRequest
            {
                Text = text,
                Title = title ?? strings["platform.share.title"],
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Share sheet failed");
        }
    }

    public async Task<bool> CopyToClipboardAsync(string text)
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => Clipboard.Default.SetTextAsync(text)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Clipboard write failed");
            return false;
        }
    }
}

/// <summary>Opens http(s)/mailto/tel links outside the app; any other scheme is refused.</summary>
public sealed class ExternalLauncher(ILogger<ExternalLauncher> logger) : IExternalLauncher
{
    public async Task OpenUriAsync(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto" or "tel"))
        {
            logger.LogWarning("Refusing to open a link with scheme {Scheme}", uri.IsAbsoluteUri ? uri.Scheme : "(relative)");
            return;
        }
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (uri.Scheme is "http" or "https")
                {
                    // Custom Tabs / SFSafariViewController: the user stays one Back away from the chat.
                    var opened = await Browser.Default.OpenAsync(uri, new BrowserLaunchOptions
                    {
                        LaunchMode = BrowserLaunchMode.SystemPreferred,
                        TitleMode = BrowserTitleMode.Show,
                    });
                    if (opened) return;
                }
                await Launcher.Default.OpenAsync(uri);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open an external link");
        }
    }
}

/// <summary>Opens a downloaded file in another app (Android: FileProvider + ACTION_VIEW; Apple: document interaction).</summary>
public sealed class FileOpener(ILogger<FileOpener> logger) : IFileOpener
{
    public bool IsSupported => true;

    public async Task<bool> OpenAsync(string localPath, string contentType)
    {
        if (!File.Exists(localPath)) return false;
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(() => Launcher.Default.OpenAsync(new OpenFileRequest
            {
                Title = Path.GetFileName(localPath),
                File = new ReadOnlyFile(localPath, string.IsNullOrEmpty(contentType) ? FileNames.ContentTypeFor(localPath) : contentType),
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No app could open the file");
            return false;
        }
    }
}
