using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Services;

/// <summary>
/// App-level foreground state. Android: ProcessLifecycleOwner (any visible activity, survives activity
/// recreation); Apple: UIApplication enter-foreground / did-enter-background notifications.
/// </summary>
public sealed class AppLifecycle : IAppLifecycle
{
    private readonly ILogger<AppLifecycle> _logger;
    private readonly object _gate = new();
    private Action<bool>? _handlers;
    private volatile bool _isForeground;
    private bool _started;
#if IOS || MACCATALYST
    private readonly List<Foundation.NSObject> _observers = [];
#endif

    public AppLifecycle(ILogger<AppLifecycle> logger)
    {
        _logger = logger;
        _isForeground = InitialForeground();
        Start();
    }

    public bool IsForeground => _isForeground;

    public event Action<bool>? ForegroundChanged
    {
        add { lock (_gate) _handlers += value; }
        remove { lock (_gate) _handlers -= value; }
    }

    internal void SetForeground(bool foreground)
    {
        if (_isForeground == foreground) return;
        _isForeground = foreground;
        Action<bool>? handlers;
        lock (_gate) handlers = _handlers;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<bool>)handler)(foreground);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foreground handler failed");
            }
        }
    }

    /// <summary>
    /// The state until <see cref="Start"/> has run on the main thread. Core copies <see cref="IsForeground"/> while it
    /// is built, which happens on a pool thread when the background connection starts the process (boot, update):
    /// Android reads the process state right away (allowed off the main thread) instead of assuming foreground.
    /// </summary>
    private static bool InitialForeground()
    {
#if ANDROID
        try
        {
            return AndroidX.Lifecycle.ProcessLifecycleOwner.Get().Lifecycle.CurrentState.IsAtLeast(AndroidX.Lifecycle.Lifecycle.State.Started!);
        }
        catch (Exception)
        {
            return true;
        }
#else
        return true;
#endif
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }
        try
        {
#if ANDROID
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var lifecycle = AndroidX.Lifecycle.ProcessLifecycleOwner.Get().Lifecycle;
                    // Raised, not just stored: whoever read the initial state before this ran must hear a change.
                    SetForeground(lifecycle.CurrentState.IsAtLeast(AndroidX.Lifecycle.Lifecycle.State.Started!));
                    lifecycle.AddObserver(new ProcessObserver(this));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ProcessLifecycleOwner unavailable; assuming foreground");
                }
            });
#elif IOS || MACCATALYST
            MainThread.BeginInvokeOnMainThread(() =>
            {
                SetForeground(UIKit.UIApplication.SharedApplication.ApplicationState != UIKit.UIApplicationState.Background);
                _observers.Add(UIKit.UIApplication.Notifications.ObserveWillEnterForeground((_, _) => SetForeground(true)));
                _observers.Add(UIKit.UIApplication.Notifications.ObserveDidBecomeActive((_, _) => SetForeground(true)));
                _observers.Add(UIKit.UIApplication.Notifications.ObserveDidEnterBackground((_, _) => SetForeground(false)));
            });
#endif
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lifecycle tracking failed to start");
        }
    }

#if ANDROID
    private sealed class ProcessObserver(AppLifecycle owner) : Java.Lang.Object, AndroidX.Lifecycle.ILifecycleEventObserver
    {
        public void OnStateChanged(AndroidX.Lifecycle.ILifecycleOwner source, AndroidX.Lifecycle.Lifecycle.Event e)
        {
            if (e == AndroidX.Lifecycle.Lifecycle.Event.OnStart) owner.SetForeground(true);
            else if (e == AndroidX.Lifecycle.Lifecycle.Event.OnStop) owner.SetForeground(false);
        }
    }
#endif
}
