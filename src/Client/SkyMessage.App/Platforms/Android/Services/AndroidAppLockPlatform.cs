using System.Runtime.Versioning;
using Android.App;
using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Droid;

/// <summary>
/// The app passcode on Android. Fingerprint or face unlock through the framework BiometricPrompt (Android 10+, no extra
/// library), allowing weak biometrics so the face unlock of most phones counts; the prompt has no device-credential
/// fallback, its button goes back to our keypad. While a passcode is set the recent-apps switcher gets no picture of the
/// content: Android 13+ takes no recents screenshot (the app can still be screenshotted while in use), older versions
/// mark the window FLAG_SECURE while the activity is paused (<see cref="AndroidScreenSecurityService"/>), the moment the
/// system takes that picture. SystemClock.elapsedRealtime is the boot clock: it counts deep sleep and cannot be set.
/// </summary>
public sealed class AndroidAppLockPlatform(AndroidScreenSecurityService screen, ILogger<AndroidAppLockPlatform> logger) : IAppLockPlatform
{
    private volatile bool _hiddenInRecents;

    /// <summary>One process, one instance of the app: nobody else to tell or to hear from.</summary>
    public void Announce(AppLockSignal signal)
    {
    }

    public event Action<AppLockSignal>? Signaled
    {
        add { }
        remove { }
    }

    public bool SupportsBiometrics => OperatingSystem.IsAndroidVersionAtLeast(29);

    public bool CanUseBiometrics
    {
        get
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return false;
            try
            {
                if (Android.App.Application.Context.GetSystemService(Context.BiometricService) is not BiometricManager manager) return false;
                var code = OperatingSystem.IsAndroidVersionAtLeast(30)
                    ? manager.CanAuthenticate((int)BiometricManagerAuthenticators.BiometricWeak)
                    : manager.CanAuthenticate();
                return code == BiometricCode.Success;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Biometric availability could not be read");
                return false;
            }
        }
    }

    public TimeSpan? SinceBoot
    {
        get
        {
            try
            {
                return TimeSpan.FromMilliseconds(SystemClock.ElapsedRealtime());
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public async Task<BiometricOutcome> AuthenticateAsync(BiometricPromptText text, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29) || !CanUseBiometrics) return BiometricOutcome.Unavailable;
        var completion = new TaskCompletionSource<BiometricOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new CancellationSignal();
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => Show(text, signal, completion)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The biometric prompt could not be shown");
            return BiometricOutcome.Unavailable;
        }

        await using (cancellationToken.Register(() =>
                     {
                         completion.TrySetResult(BiometricOutcome.Cancelled);
                         MainThread.BeginInvokeOnMainThread(() => Cancel(signal));
                     }))
        {
            return await completion.Task.ConfigureAwait(false);
        }
    }

    /// <summary>.NET's PBKDF2 already runs in the platform's crypto on Android (one native call for all rounds).</summary>
    public Task<byte[]?> DeriveKeyAsync(string passcode, byte[] salt, int iterations, int length) => Task.FromResult<byte[]?>(null);

    public void SetHiddenInRecents(bool hidden)
    {
        _hiddenInRecents = hidden;
        // Before Android 13 the only way is a secure window at the moment the picture is taken.
        screen.SetSecureWhilePaused(hidden && !OperatingSystem.IsAndroidVersionAtLeast(33));
        MainThread.BeginInvokeOnMainThread(() => Apply(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity));
    }

    /// <summary>UI thread. Also called by MainActivity on create (a recreated activity has a fresh window).</summary>
    internal void Apply(Activity? activity)
    {
        if (activity is null || !OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        try
        {
            activity.SetRecentsScreenshotEnabled(!_hiddenInRecents);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The recents screenshot setting could not be changed");
        }
    }

    /// <summary>UI thread.</summary>
    private void Show(BiometricPromptText text, CancellationSignal signal, TaskCompletionSource<BiometricOutcome> completion)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return;
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null || activity.IsFinishing || activity.IsDestroyed || activity.MainExecutor is not { } executor)
        {
            completion.TrySetResult(BiometricOutcome.Unavailable);
            return;
        }
        var builder = new BiometricPrompt.Builder(activity);
        builder.SetTitle(text.Title);
        if (!string.IsNullOrWhiteSpace(text.Subtitle)) builder.SetSubtitle(text.Subtitle);
        builder.SetNegativeButton(text.CancelText, executor, new NegativeButton(completion));
        // Face unlock completes on its own, without a second tap.
        builder.SetConfirmationRequired(false);
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) builder.SetAllowedAuthenticators((int)BiometricManagerAuthenticators.BiometricWeak);
        builder.Build().Authenticate(signal, executor, new Callback(completion));
    }

    private static void Cancel(CancellationSignal signal)
    {
        try
        {
            signal.Cancel();
        }
        catch (Exception)
        {
            // The prompt is gone already.
        }
    }

    /// <summary>Only created on Android 10+ (<see cref="Show"/>).</summary>
    [SupportedOSPlatform("android29.0")]
    private sealed class Callback(TaskCompletionSource<BiometricOutcome> completion) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result) =>
            completion.TrySetResult(BiometricOutcome.Succeeded);

        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString) =>
            completion.TrySetResult(errorCode switch
            {
                BiometricErrorCode.Lockout or BiometricErrorCode.LockoutPermanent => BiometricOutcome.LockedOut,
                BiometricErrorCode.UserCanceled or BiometricErrorCode.Canceled or BiometricErrorCode.Timeout => BiometricOutcome.Cancelled,
                _ => BiometricOutcome.Unavailable,
            });

        // OnAuthenticationFailed: a finger or face that did not match; the prompt stays and says so itself.
    }

    private sealed class NegativeButton(TaskCompletionSource<BiometricOutcome> completion) : Java.Lang.Object, IDialogInterfaceOnClickListener
    {
        public void OnClick(IDialogInterface? dialog, int which) => completion.TrySetResult(BiometricOutcome.Cancelled);
    }
}
