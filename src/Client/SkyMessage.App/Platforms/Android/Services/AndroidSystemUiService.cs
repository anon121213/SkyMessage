using Android.Views;
using AndroidX.Core.View;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using Color = Android.Graphics.Color;

namespace SkyMessage.App.Droid;

/// <summary>
/// The app draws edge-to-edge on every Android version: both system bars are transparent and the CSS paints
/// behind them (insets arrive as --sm-safe-*). Only the icon tint follows the in-app theme; the background
/// colour is applied to the window/WebView background to avoid flashes while the page (re)loads.
/// </summary>
public sealed class AndroidSystemUiService(ILogger<AndroidSystemUiService> logger) : ISystemUiService
{
    // Until the UI applies its theme, follow the system night mode (avoids a white flash in dark mode).
    private bool _darkContent = !IsSystemNight();
    private Color _background = IsSystemNight() ? Color.ParseColor("#150F38") : Color.ParseColor("#ECE6FF");

    public Task SetSystemBarsStyleAsync(bool darkContent, string backgroundHex)
    {
        _darkContent = darkContent;
        if (TryParse(backgroundHex, out var color)) _background = color;
        MainThread.BeginInvokeOnMainThread(() => Apply(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity));
        return Task.CompletedTask;
    }

    /// <summary>Also called by MainActivity on create / configuration changes.</summary>
    internal void Apply(Android.App.Activity? activity)
    {
        var window = activity?.Window;
        if (window is null) return;
        try
        {
            WindowCompat.SetDecorFitsSystemWindows(window, false);
            if (!OperatingSystem.IsAndroidVersionAtLeast(35))
            {
#pragma warning disable CA1422 // Ignored on Android 15+, required below it for transparent bars.
                window.SetStatusBarColor(Color.Transparent);
                window.SetNavigationBarColor(Color.Transparent);
#pragma warning restore CA1422
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                window.NavigationBarContrastEnforced = false;
                if (!OperatingSystem.IsAndroidVersionAtLeast(35)) window.StatusBarContrastEnforced = false;
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(28) && window.Attributes is { } attributes)
            {
                attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                window.Attributes = attributes;
            }

            var controller = WindowCompat.GetInsetsController(window, window.DecorView);
            if (controller is not null)
            {
                controller.AppearanceLightStatusBars = _darkContent;
                controller.AppearanceLightNavigationBars = _darkContent;
            }
            window.DecorView.SetBackgroundColor(_background);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "System bar styling failed");
        }
    }

    internal Color Background => _background;

    private static bool IsSystemNight()
    {
        try
        {
            var mode = Android.App.Application.Context.Resources?.Configuration?.UiMode ?? Android.Content.Res.UiMode.NightNo;
            return (mode & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryParse(string? hex, out Color color)
    {
        color = Color.White;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            color = Color.ParseColor(hex.StartsWith('#') ? hex : "#" + hex);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>System haptics through the view hierarchy (respects the user's touch-feedback setting; no VIBRATE needed).</summary>
public sealed class AndroidHapticsService : IHapticsService
{
    public bool IsSupported => true;

    public void Perform(HapticKind kind)
    {
        var constant = kind switch
        {
            HapticKind.Light => FeedbackConstants.KeyboardTap,
            HapticKind.Medium => FeedbackConstants.VirtualKey,
            HapticKind.Heavy => FeedbackConstants.LongPress,
            HapticKind.Selection => OperatingSystem.IsAndroidVersionAtLeast(27) ? FeedbackConstants.TextHandleMove : FeedbackConstants.ClockTick,
            HapticKind.Success => OperatingSystem.IsAndroidVersionAtLeast(30) ? FeedbackConstants.Confirm : FeedbackConstants.VirtualKey,
            HapticKind.Warning => OperatingSystem.IsAndroidVersionAtLeast(30) ? FeedbackConstants.Reject : FeedbackConstants.LongPress,
            _ => FeedbackConstants.KeyboardTap,
        };
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window?.DecorView?.PerformHapticFeedback(constant);
            }
            catch (Exception)
            {
                // Haptics are decoration.
            }
        });
    }
}
