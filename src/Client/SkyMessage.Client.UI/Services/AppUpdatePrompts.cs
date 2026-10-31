using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.UI.Shell;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// What the update card (<see cref="UpdateNotice"/>) and Settings › About do with <see cref="IAppUpdateService"/>:
/// "Update" asks for the install permission through our explainer first, failures read as plain sentences, and a
/// build Android refuses can still be downloaded by hand.
/// </summary>
public sealed class AppUpdatePrompts(
    IAppUpdateService updates,
    DialogService dialogs,
    IPreferencesStore preferences,
    IExternalLauncher launcher,
    IServerEndpointService endpoints,
    ILocalizer localizer,
    ILogger<AppUpdatePrompts> logger)
{
    /// <summary>Per-device flag: the explainer was shown once by itself (afterwards only when the user taps Update).</summary>
    public const string PermissionAskedKey = "ui.updates.installPermission.asked.v1";

    /// <summary>Installs the downloaded build; without the install permission the explainer comes first.</summary>
    public async Task InstallAsync()
    {
        try
        {
            if (updates.Status.NeedsInstallPermission)
            {
                await AskPermissionAsync();
                return;
            }
            await updates.InstallNowAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Starting the update failed");
        }
    }

    /// <summary>
    /// Our explainer, then (if the user agrees) the system screen "Install unknown apps"; the updater carries on with
    /// the install once it is allowed. True when the user went to allow it.
    /// </summary>
    public async Task<bool> AskPermissionAsync()
    {
        await RememberAskedAsync();
        var result = await dialogs.ShowAsync<InstallPermissionSheet>(localizer["app.update.permission.title"],
            options: new DialogOptions { Size = DialogSize.Medium, Presentation = OverlayPresentation.Sheet });
        if (result is not true) return false;
        await updates.RequestInstallPermissionAsync();
        return true;
    }

    /// <summary>Shows the explainer by itself once per device, when the first update is ready and installs are not allowed.</summary>
    public async Task AskPermissionOnceAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(await preferences.GetAsync(PermissionAskedKey))) return;
            await AskPermissionAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The install permission explainer failed");
        }
    }

    /// <summary>The current APK in the browser (a build Android refuses to install over this one).</summary>
    public async Task DownloadManuallyAsync()
    {
        try
        {
            await launcher.OpenUriAsync(new Uri(endpoints.BaseUri, ApiRoutes.LatestApk));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Opening the download link failed");
        }
    }

    /// <summary>Android refused this build for good: retrying cannot help, a manual reinstall can.</summary>
    public static bool IsPermanent(string? error) =>
        error is AppUpdateErrors.Conflict or AppUpdateErrors.Incompatible or AppUpdateErrors.Blocked;

    /// <summary>Why the last attempt failed, as a sentence for people.</summary>
    public string ErrorText(string? error) => error switch
    {
        AppUpdateErrors.Conflict => localizer["app.update.error.conflict"],
        AppUpdateErrors.Incompatible => localizer["app.update.error.incompatible"],
        AppUpdateErrors.Blocked => localizer["app.update.error.blocked"],
        AppUpdateErrors.Storage => localizer["app.update.error.storage"],
        AppUpdateErrors.Integrity or AppUpdateErrors.Invalid => localizer["app.update.error.integrity"],
        AppUpdateErrors.InstallFailed => localizer["app.update.error.install"],
        _ => localizer["app.update.error.network"],
    };

    private async Task RememberAskedAsync()
    {
        try
        {
            await preferences.SetAsync(PermissionAskedKey, "1");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not remember the install permission explainer");
        }
    }
}
