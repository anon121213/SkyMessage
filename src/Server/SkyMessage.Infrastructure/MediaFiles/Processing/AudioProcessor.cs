using System.Globalization;
using Microsoft.Extensions.Logging;
using SkyMessage.Contracts;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <param name="DurationMs">Null when the container reports none (the client's value is kept then).</param>
/// <param name="Waveform">Null when the PCM could not be decoded (cosmetic: the voice message itself is fine).</param>
internal sealed record VoiceOutput(string Path, int? DurationMs, byte[]? Waveform);

/// <summary>Voice messages → AAC-LC 48 kbps mono 48 kHz in .m4a (plays everywhere) + peak waveform; audio files → duration.</summary>
internal sealed class AudioProcessor(FfmpegRunner ffmpeg, ILogger<AudioProcessor> logger)
{
    /// <summary>Demuxers accepted for uploaded audio and recordings (browsers record WebM/Ogg/MP4).</summary>
    internal const string InputFormats = "mov,matroska,ogg,mp3,wav,flac,aac,amr,caf,aiff,w64,wv,asf,au,ape";

    private const string OutputFormats = "mov";

    /// <summary>The waveform only needs the envelope; 8 kHz keeps an hour of PCM at ~58 MB streamed, never buffered.</summary>
    private const int WaveformSampleRate = 8000;

    public async Task<VoiceOutput> ProcessVoiceAsync(string inputPath, string workDirectory, CancellationToken cancellationToken)
    {
        var probe = await ffmpeg.ProbeAsync(inputPath, InputFormats, cancellationToken);
        if (probe.Audio is null) throw new MediaProcessingException("The recording has no audio stream.");

        var outputPath = Path.Combine(workDirectory, "voice.m4a");
        await ffmpeg.RunFfmpegAsync(
            [
                "-protocol_whitelist", "file", "-format_whitelist", InputFormats, "-i", inputPath,
                "-vn", "-map", "0:a:0",
                "-t", Limits.VoiceMaxDurationSeconds.ToString(CultureInfo.InvariantCulture),
                "-c:a", "aac", "-profile:a", "aac_low", "-b:a", "48k", "-ac", "1", "-ar", "48000",
                "-map_metadata", "-1", "-map_chapters", "-1", "-movflags", "+faststart",
                "-f", "mp4", outputPath,
            ],
            MediaTimeouts.Voice, cancellationToken);

        var result = await ffmpeg.ProbeAsync(outputPath, OutputFormats, cancellationToken);
        if (result.Audio is null) throw new MediaProcessingException("The processed recording has no audio stream.");
        byte[]? waveform = null;
        try
        {
            waveform = await ComputeWaveformAsync(outputPath, OutputFormats, cancellationToken);
        }
        catch (MediaProcessingException ex)
        {
            logger.LogInformation("Waveform could not be computed: {Error}", ex.Message);
        }
        return new VoiceOutput(outputPath, result.DurationMs, waveform);
    }

    /// <summary>Duration of an audio file, or null when it has no audio stream.</summary>
    public async Task<int?> ProbeDurationAsync(string inputPath, CancellationToken cancellationToken)
    {
        var probe = await ffmpeg.ProbeAsync(inputPath, InputFormats, cancellationToken);
        return probe.Audio is null ? null : probe.DurationMs;
    }

    /// <summary>Decodes to mono s16le PCM on stdout and folds it into <see cref="Limits.WaveformSamples"/> peaks.</summary>
    public async Task<byte[]> ComputeWaveformAsync(string inputPath, string formatWhitelist, CancellationToken cancellationToken)
    {
        var builder = new WaveformBuilder();
        await ffmpeg.RunFfmpegAsync(
            [
                "-protocol_whitelist", "file", "-format_whitelist", formatWhitelist, "-i", inputPath,
                "-vn", "-map", "0:a:0", "-ac", "1", "-ar", WaveformSampleRate.ToString(CultureInfo.InvariantCulture),
                "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1",
            ],
            async (stdout, token) =>
            {
                var buffer = new byte[32 * 1024];
                int read;
                while ((read = await stdout.ReadAsync(buffer, token)) > 0)
                    builder.AddPcm16(buffer.AsSpan(0, read));
            },
            MediaTimeouts.Waveform, cancellationToken);
        return builder.Build(Limits.WaveformSamples);
    }
}
