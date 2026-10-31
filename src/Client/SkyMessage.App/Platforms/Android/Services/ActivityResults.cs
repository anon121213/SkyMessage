using AndroidX.Activity;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;

namespace SkyMessage.App.Droid;

/// <summary>
/// Awaitable Activity Result API launches through the current activity's <see cref="ActivityResultRegistry"/>
/// (registered with a unique key and unregistered after the result, so they work at any time, not only
/// before the activity is started).
/// </summary>
internal static class ActivityResults
{
    public static async Task<Java.Lang.Object?> LaunchAsync(ActivityResultContract contract, Java.Lang.Object? input, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<Java.Lang.Object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ActivityResultLauncher? launcher = null;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is not ComponentActivity activity)
                throw new InvalidOperationException("No foreground activity to launch from");
            var key = "sky." + Guid.NewGuid().ToString("N");
            launcher = activity.ActivityResultRegistry.Register(key, contract, new Callback(result =>
            {
                tcs.TrySetResult(result);
            }));
            launcher.Launch(input);
        }).ConfigureAwait(false);

        try
        {
            await using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    launcher?.Unregister();
                }
                catch (Exception)
                {
                }
            });
        }
    }

    private sealed class Callback(Action<Java.Lang.Object?> onResult) : Java.Lang.Object, IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result) => onResult(result);
    }
}
