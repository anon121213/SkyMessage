using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Counts the screens that show protected content: the first handle turns the platform's screen security on, the last
/// one released turns it off again. Handles from screens that come and go out of order (a chat opened over another
/// one, a page disposed after the next one rendered) keep the window secure until the last of them is gone. The
/// platform call happens under the lock (it only posts to the UI thread), so the calls it gets follow the count.
/// </summary>
internal sealed class ScreenCaptureGuard(IScreenSecurityService platform, ILogger<ScreenCaptureGuard> logger) : IScreenCaptureGuard
{
    private readonly object _lock = new();
    private int _holders;

    public bool IsSupported => platform.IsSupported;

    public bool IsSecure
    {
        get
        {
            lock (_lock) return _holders > 0;
        }
    }

    public IDisposable Protect()
    {
        lock (_lock)
        {
            if (++_holders == 1) Apply(true);
        }
        return new Handle(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            if (--_holders == 0) Apply(false);
        }
    }

    private void Apply(bool secure)
    {
        try
        {
            platform.SetSecure(secure);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Screen security could not be changed");
        }
    }

    private sealed class Handle(ScreenCaptureGuard owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release();
        }
    }
}
