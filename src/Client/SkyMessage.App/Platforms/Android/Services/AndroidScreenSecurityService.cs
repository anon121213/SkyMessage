using Android.Views;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Droid;

/// <summary>
/// FLAG_SECURE on the activity window while a protected chat is open or the app passcode locks the app: screenshots and
/// screen recordings come out black, casting shows nothing and the recents preview is blank. The flag can change on a
/// visible window (no restart) on every supported API level (26+); it needs no permission. The last requested state is
/// kept so a recreated activity (MainActivity.OnCreate) gets it back. Before Android 13, while an app passcode is set, the
/// window is also secure whenever the activity is paused (<see cref="SetSecureWhilePaused"/>, driven by MainActivity):
/// the system takes the recents picture right after the pause, and those versions cannot skip it otherwise.
/// </summary>
public sealed class AndroidScreenSecurityService(ILogger<AndroidScreenSecurityService> logger) : IScreenSecurityService
{
    private volatile bool _secure;
    private volatile bool _secureWhilePaused;
    private volatile bool _paused;

    public bool IsSupported => true;

    public void SetSecure(bool secure)
    {
        _secure = secure;
        MainThread.BeginInvokeOnMainThread(() => Apply(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity));
    }

    /// <summary>Android 12 and older with a passcode set: keep the content out of the recents picture.</summary>
    internal void SetSecureWhilePaused(bool secure)
    {
        _secureWhilePaused = secure;
        MainThread.BeginInvokeOnMainThread(() => Apply(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity));
    }

    /// <summary>UI thread: MainActivity's OnPause / OnResume. Applied at once (the picture is taken right after the pause).</summary>
    internal void SetPaused(Android.App.Activity activity, bool paused)
    {
        _paused = paused;
        Apply(activity);
    }

    /// <summary>UI thread. Also called by MainActivity on create.</summary>
    internal void Apply(Android.App.Activity? activity)
    {
        var window = activity?.Window;
        if (window is null) return;
        try
        {
            // Read at apply time: of two quick changes posted in a row both apply the latest state.
            if (_secure || (_paused && _secureWhilePaused)) window.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
            else window.ClearFlags(WindowManagerFlags.Secure);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FLAG_SECURE could not be changed");
        }
    }
}
