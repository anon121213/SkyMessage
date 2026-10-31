using Android.App;
using Android.Runtime;
using SkyMessage.App.Droid;
using SkyMessage.App.Services;

namespace SkyMessage.App;

#if DEBUG
// Development servers are plain http (http://10.0.2.2:8080/). Release builds only talk https.
[Application(Name = "com.skymessage.messenger.MainApplication", UsesCleartextTraffic = true)]
#else
[Application(Name = "com.skymessage.messenger.MainApplication")]
#endif
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override void OnCreate()
    {
        base.OnCreate();
        try
        {
            AppServices.Get<NotificationChannels>()?.EnsureBase();
        }
        catch (Exception)
        {
            // Channels are re-ensured before every notification.
        }
        Task.Run(AppPaths.CleanupTemp);
    }
}
