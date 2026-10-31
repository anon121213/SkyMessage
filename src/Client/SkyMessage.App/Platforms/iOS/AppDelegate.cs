using Foundation;
using SkyMessage.App.Apple;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using UIKit;
using UserNotifications;

namespace SkyMessage.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        // Before MAUI starts, so a notification tap that launched the app is delivered.
        UNUserNotificationCenter.Current.Delegate = AppleNotificationDelegate.Instance;
        var started = base.FinishedLaunching(application, launchOptions);
        Task.Run(AppPaths.CleanupTemp);
        return started;
    }

    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        if (AppServices.Get<IDeepLinkSource>() is DeepLinkSource links && url.AbsoluteString is { } link)
        {
            links.Publish(link);
            return true;
        }
        return base.OpenUrl(application, url, options);
    }

    public override bool ContinueUserActivity(UIApplication application, NSUserActivity userActivity, UIApplicationRestorationHandler completionHandler)
    {
        if (userActivity.ActivityType == NSUserActivityType.BrowsingWeb
            && userActivity.WebPageUrl?.AbsoluteString is { } link
            && AppServices.Get<IDeepLinkSource>() is DeepLinkSource links)
        {
            links.Publish(link);
            return true;
        }
        return base.ContinueUserActivity(application, userActivity, completionHandler);
    }
}
