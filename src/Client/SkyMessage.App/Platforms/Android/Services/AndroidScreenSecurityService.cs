using Android.Views;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Droid;

/// <summary>
/// FLAG_SECURE on the activity window while a protected chat is open or the app passcode locks the app: screenshots and
/// screen recordings come out black, casting shows nothing and the recents preview is blank. The flag can change on a
/// visible window (no restart) on every supported API level (26+); it needs no permission. The last requested state is
/// kept so a recreated activity (MainActivity.OnCreate) gets it back. Before Android 13 the window also stays secure for
/// as long as an app passcode is set (<see cref="SetSecureWhilePasscodeSet"/>): those versions take the recents picture
/// before the app can react — on the way out, and with gesture navigation even while the app is still resumed — and a
/// window that is secure all along is the only one they leave out of it.
/// </summary>
public sealed class AndroidScreenSecurityService(ILogger<AndroidScreenSecurityService> logger) : IScreenSecurityService
{
    private volatile bool _secure;
    private volatile bool _passcodeSet;

    public bool IsSupported => true;

    public void SetSecure(bool secure)
    {
        _secure = secure;
        MainThread.BeginInvokeOnMainThread(() => Apply(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity));
    }

    /// <summary>Android 12 and older with a passcode set: keep the content out of the recents picture.</summary>
    internal void SetSecureWhilePasscodeSet(bool secure)
    {
        _passcodeSet = secure;
        MainThread.BeginInvokeOnMainThread(() => Apply(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity));
    }

    /// <summary>UI thread. Also called by MainActivity on create.</summary>
    internal void Apply(Android.App.Activity? activity)
    {
        var window = activity?.Window;
        if (window is null) return;
        try
        {
            // Read at apply time: of two quick changes posted in a row both apply the latest state.
            if (_secure || _passcodeSet) window.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
            else window.ClearFlags(WindowManagerFlags.Secure);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FLAG_SECURE could not be changed");
        }
    }
}
