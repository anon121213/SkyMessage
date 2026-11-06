using Android.Database;
using Android.OS;
using Android.Provider;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Droid;

/// <summary>
/// Raises <see cref="Changed"/> when the phone book changes: a ContentObserver on the contacts provider (registered
/// only while contact sync is on; changes come in bursts and Core debounces them).
/// </summary>
public sealed class AndroidDeviceContactsWatcher(ILogger<AndroidDeviceContactsWatcher> logger) : IDeviceContactsWatcher
{
    private readonly object _gate = new();
    private Observer? _observer;

    public bool IsSupported => true;

    public event Action? Changed;

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            var resolver = Android.App.Application.Context.ContentResolver;
            if (resolver is null) return;
            if (!enabled)
            {
                if (_observer is null) return;
                resolver.UnregisterContentObserver(_observer);
                _observer.Dispose();
                _observer = null;
                return;
            }
            if (_observer is not null) return;
            try
            {
                var observer = new Observer(this);
                resolver.RegisterContentObserver(ContactsContract.Contacts.ContentUri!, true, observer);
                _observer = observer;
            }
            catch (Exception ex)
            {
                // No permission (yet): the next start syncs anyway.
                logger.LogInformation(ex, "Phone book watcher not registered");
            }
        }
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Phone book change handler failed");
        }
    }

    private sealed class Observer(AndroidDeviceContactsWatcher owner) : ContentObserver(new Handler(Looper.MainLooper!))
    {
        public override void OnChange(bool selfChange) => owner.Raise();
    }
}
