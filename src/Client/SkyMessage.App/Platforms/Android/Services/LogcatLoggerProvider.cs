using Microsoft.Extensions.Logging;
using AndroidLog = Android.Util.Log;

namespace SkyMessage.App.Droid;

/// <summary>
/// Writes warnings and errors to logcat (tag "SkyMessage") in release builds, so a problem on a family member's
/// phone can be diagnosed with <c>adb logcat -s SkyMessage</c>. Debug builds use the Debug provider instead.
/// </summary>
internal sealed class LogcatLoggerProvider : ILoggerProvider
{
    private const string Tag = "SkyMessage";

    public ILogger CreateLogger(string categoryName) => new LogcatLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class LogcatLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = $"{category}: {formatter(state, exception)}";
            if (exception is not null) message += Environment.NewLine + exception;
            if (logLevel >= LogLevel.Error) AndroidLog.Error(Tag, message);
            else AndroidLog.Warn(Tag, message);
        }
    }
}
