using System.Reflection;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core;
using SkyMessage.Client.UI;
using SkyMessage.Contracts;

namespace SkyMessage.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#else
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
#if ANDROID
        builder.Logging.AddProvider(new Droid.LogcatLoggerProvider());
#endif
#endif

        builder.Services.AddSkyMessageClientCore(options =>
        {
            options.DefaultServerUrl = DefaultServerUrl();
            options.MediaUrlMode = MediaUrlMode.AppProxy;
#if IOS || MACCATALYST
            // MAUI's WKURLSchemeHandler bridge buffers every response body in memory (NSData.FromStream) before
            // WebKit sees a byte, so <video> and long audio cannot stream or seek through the app-origin proxy:
            // those kinds use signed server URLs (the WebView's own stack, proxied on iOS/Mac 17+). Photos, voice
            // notes and files stay small and keep the cache/failover benefits of the proxy.
            options.AppProxyKinds = [MediaKind.Photo, MediaKind.Voice, MediaKind.File];
#endif
        });
        builder.Services.AddSkyMessagePlatform();
        builder.Services.AddSkyMessageUI();
        builder.Services.AddTransient<MainPage>();

        return builder.Build();
    }

    /// <summary>
    /// Build-time server (-p:SkyMessageDefaultServerUrl=...), else the local development server: the Android
    /// emulator reaches the host at 10.0.2.2; a USB device uses localhost through `adb reverse tcp:8080 tcp:8080`;
    /// simulators and Macs use localhost.
    /// </summary>
    private static string DefaultServerUrl()
    {
        var configured = typeof(MauiProgram).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SkyMessageDefaultServerUrl")?.Value;
        if (!string.IsNullOrWhiteSpace(configured)) return configured.EndsWith('/') ? configured : configured + "/";
#if ANDROID
        try
        {
            if (DeviceInfo.Current.DeviceType == DeviceType.Virtual) return "http://10.0.2.2:8080/";
        }
        catch (Exception)
        {
            return "http://10.0.2.2:8080/";
        }
#endif
        return "http://localhost:8080/";
    }
}
