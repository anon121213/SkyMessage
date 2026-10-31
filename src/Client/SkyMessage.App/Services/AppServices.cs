using Microsoft.Extensions.DependencyInjection;

namespace SkyMessage.App.Services;

/// <summary>
/// Access to the app's root service provider for objects the OS creates on its own (Android services,
/// broadcast receivers, notification delegates). Null until MAUI has built the app.
/// </summary>
internal static class AppServices
{
    public static IServiceProvider? Provider => IPlatformApplication.Current?.Services;

    public static T? Get<T>() where T : class
    {
        try
        {
            return Provider?.GetService<T>();
        }
        catch (Exception)
        {
            // Resolving during app shutdown (disposed provider) must never crash an OS callback.
            return null;
        }
    }
}
