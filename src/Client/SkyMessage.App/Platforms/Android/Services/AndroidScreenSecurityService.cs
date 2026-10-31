using Android.Views;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Droid;

/// <summary>
/// FLAG_SECURE on the activity window while a protected chat is open: screenshots and screen recordings come out
/// black, casting shows nothing and the recents preview is blank. The flag can change on a visible window (no
/// restart) on every supported API level (26+); it needs no permission. The last requested state is kept so a
/// recreated activity (MainActivity.OnCreate) gets it back.
/// </summary>
public sealed class AndroidScreenSecurityService(ILogger<AndroidScreenSecurityService> logger) : IScreenSecurityService
{
    private volatile bool _secure;

    public bool IsSupported => true;

    public void SetSecure(bool secure)
    {
        _secure = secure;
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
            if (_secure) window.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
            else window.ClearFlags(WindowManagerFlags.Secure);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FLAG_SECURE could not be changed");
        }
    }
}
