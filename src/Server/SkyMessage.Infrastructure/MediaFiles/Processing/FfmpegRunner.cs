using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Options;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <summary>ffmpeg/ffprobe could not be started: processing stores the upload as-is instead.</summary>
internal sealed class MediaToolMissingException(string tool, string path, Exception? inner)
    : Exception($"{tool} could not be started ('{path}'). Install FFmpeg or set MediaProcessing:{(tool == "ffprobe" ? "FfprobePath" : "FfmpegPath")}.", inner)
{
    public string Tool { get; } = tool;
}

/// <summary>The input cannot be processed (unsupported/corrupt file, tool failure, timeout). The message is stored as ProcessingError.</summary>
internal sealed class MediaProcessingException(string message, Exception? inner = null) : Exception(message, inner);

internal static class MediaTimeouts
{
    public static readonly TimeSpan Probe = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan StillImage = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Remux = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Voice = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Waveform = TimeSpan.FromMinutes(5);

    /// <summary>Generous for slow servers: 4× real time + 10 min, capped at 6 h.</summary>
    public static TimeSpan Transcode(double? durationSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp((durationSeconds ?? 0) * 4 + 600, 600, 6 * 3600));
}

/// <summary>
/// Runs ffmpeg/ffprobe as external processes (never linked): arguments through <see cref="ProcessStartInfo.ArgumentList"/>
/// (no shell), stdin closed, stderr tail captured for errors, hard timeout and cancellation that kill the process tree.
/// Callers restrict inputs with <c>-protocol_whitelist file</c> and a demuxer <c>-format_whitelist</c> so a crafted
/// upload (e.g. an HLS playlist named .mp4) cannot make ffmpeg read other local files or the network.
/// </summary>
internal sealed class FfmpegRunner
{
    private const int StderrTailChars = 1500;
    private const int MaxProbeOutputChars = 8 * 1024 * 1024;
    private static readonly string[] WellKnownDirectories = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"];

    private readonly ILogger<FfmpegRunner> _logger;

    public FfmpegRunner(IOptions<MediaProcessingOptions> options, ILogger<FfmpegRunner> logger)
    {
        _logger = logger;
        FfmpegPath = ResolveExecutable(options.Value.FfmpegPath, "ffmpeg");
        FfprobePath = ResolveExecutable(options.Value.FfprobePath, "ffprobe");
    }

    public string FfmpegPath { get; }
    public string FfprobePath { get; }

    /// <summary>Runs ffmpeg with <c>-nostdin -hide_banner -v error -y</c> prepended; throws on failure.</summary>
    public Task RunFfmpegAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunAsync("ffmpeg", FfmpegPath, WithFfmpegDefaults(arguments), readStdout: null, timeout, cancellationToken);

    /// <summary>As <see cref="RunFfmpegAsync(IReadOnlyList{string}, TimeSpan, CancellationToken)"/>, streaming stdout (e.g. raw PCM on <c>pipe:1</c>) to <paramref name="readStdout"/>.</summary>
    public Task RunFfmpegAsync(IReadOnlyList<string> arguments, Func<Stream, CancellationToken, Task> readStdout, TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunAsync("ffmpeg", FfmpegPath, WithFfmpegDefaults(arguments), readStdout, timeout, cancellationToken);

    /// <summary>ffprobe <c>-show_format -show_streams</c> of a local file, limited to the given demuxers.</summary>
    public async Task<MediaProbe> ProbeAsync(string path, string formatWhitelist, CancellationToken cancellationToken)
    {
        string? json = null;
        await RunAsync("ffprobe", FfprobePath,
            [
                "-v", "error", "-hide_banner",
                "-protocol_whitelist", "file", "-format_whitelist", formatWhitelist,
                "-print_format", "json", "-show_format", "-show_streams",
                "-i", path,
            ],
            async (stdout, token) => json = await ReadTextAsync(stdout, MaxProbeOutputChars, token),
            MediaTimeouts.Probe, cancellationToken);
        try
        {
            return MediaProbe.Parse(json ?? "");
        }
        catch (JsonException ex)
        {
            throw new MediaProcessingException("ffprobe returned unreadable output.", ex);
        }
    }

    internal static string ResolveExecutable(string? configured, string defaultName)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? defaultName : configured.Trim();
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)) return name;

        // Services started outside a login shell (launchd, systemd) often lack /opt/homebrew/bin or /usr/local/bin on PATH.
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(OperatingSystem.IsWindows() ? [] : WellKnownDirectories);
        string[] candidates = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? [name + ".exe", name] : [name];
        foreach (var directory in directories)
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path)) return path;
            }
        }
        return name;
    }

    private static List<string> WithFfmpegDefaults(IReadOnlyList<string> arguments) =>
        ["-nostdin", "-hide_banner", "-v", "error", "-y", .. arguments];

    private async Task RunAsync(string tool, string executable, IReadOnlyList<string> arguments,
        Func<Stream, CancellationToken, Task>? readStdout, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new MediaToolMissingException(tool, executable, null);
        }
        catch (Win32Exception ex)
        {
            throw new MediaToolMissingException(tool, executable, ex);
        }

        var started = Stopwatch.GetTimestamp();
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        var stderr = new StringBuilder();
        var stderrTask = ReadStderrTailAsync(process.StandardError, stderr, token);
        var stdoutTask = ConsumeStdoutAsync(process, readStdout, token);
        try
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await ObserveAsync(stdoutTask, stderrTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await ObserveAsync(stdoutTask, stderrTask);
            throw new MediaProcessingException($"{tool} timed out after {timeout.TotalSeconds:0} s.");
        }
        catch
        {
            Kill(process);
            await ObserveAsync(stdoutTask, stderrTask);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var detail = Squash(stderr.ToString());
            throw new MediaProcessingException(detail.Length == 0
                ? $"{tool} failed with exit code {process.ExitCode}."
                : $"{tool} failed with exit code {process.ExitCode}: {detail}");
        }

        _logger.LogDebug("{Tool} finished in {ElapsedMs} ms", tool, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static async Task ConsumeStdoutAsync(Process process, Func<Stream, CancellationToken, Task>? readStdout, CancellationToken token)
    {
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            if (readStdout is null)
                await stdout.CopyToAsync(Stream.Null, token);
            else
                await readStdout(stdout, token);
        }
        catch
        {
            // A failing consumer must not leave the process blocked on a full pipe until the timeout.
            Kill(process);
            throw;
        }
    }

    private static async Task ReadStderrTailAsync(StreamReader reader, StringBuilder tail, CancellationToken token)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > StderrTailChars * 2) tail.Remove(0, tail.Length - StderrTailChars);
        }
    }

    private static async Task<string> ReadTextAsync(Stream stream, int maxChars, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var builder = new StringBuilder();
        var buffer = new char[16384];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (builder.Length + read > maxChars) throw new MediaProcessingException("ffprobe output is too large.");
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }

    private static string Squash(string text)
    {
        var flattened = string.Join(" | ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flattened.Length <= StderrTailChars ? flattened : flattened[^StderrTailChars..];
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static async Task ObserveAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Already failing; these only carry secondary errors (pipe closed by the kill, cancellation).
        }
    }
}
