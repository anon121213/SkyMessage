using System.Globalization;
using System.Text.Json;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <summary>The parts of <c>ffprobe -print_format json -show_format -show_streams</c> that processing needs.</summary>
internal sealed record MediaProbe(string FormatName, double? DurationSeconds, IReadOnlyList<ProbeStream> Streams)
{
    /// <summary>First real video stream (cover art in audio files is not video).</summary>
    public ProbeStream? Video => Streams.FirstOrDefault(s => s.IsVideo);

    public ProbeStream? Audio => Streams.FirstOrDefault(s => s.IsAudio);

    /// <summary>ffmpeg's "mov,mp4,m4a,3gp,3g2,mj2" demuxer: MP4 and QuickTime files.</summary>
    public bool IsMp4Family => FormatName.Split(',').Any(n => n is "mp4" or "mov");

    public int? DurationMs => DurationSeconds is > 0 and < int.MaxValue / 1000.0 ? (int)Math.Round(DurationSeconds.Value * 1000) : null;

    public static MediaProbe Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("ffprobe output is not an object.");

        var format = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
        var streams = new List<ProbeStream>();
        if (root.TryGetProperty("streams", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object) streams.Add(ParseStream(element));
            }
        }

        var duration = Number(format, "duration");
        if (duration is null or <= 0)
            duration = streams.Select(s => s.DurationSeconds).Where(d => d is > 0).DefaultIfEmpty(null).Max();
        return new MediaProbe(Text(format, "format_name") ?? "", duration, streams);
    }

    private static ProbeStream ParseStream(JsonElement s)
    {
        var rotation = 0.0;
        if (s.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && Number(item, "rotation") is { } r)
                {
                    rotation = r;
                    break;
                }
            }
        }
        if (rotation == 0 && s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object && Number(tags, "rotate") is { } tag)
            rotation = tag;

        var attachedPicture = s.TryGetProperty("disposition", out var disposition) && disposition.ValueKind == JsonValueKind.Object
                              && Number(disposition, "attached_pic") is 1;

        return new ProbeStream(
            Index: (int)(Number(s, "index") ?? 0),
            CodecType: Text(s, "codec_type") ?? "",
            CodecName: Text(s, "codec_name") ?? "",
            Width: (int)(Number(s, "width") ?? 0),
            Height: (int)(Number(s, "height") ?? 0),
            PixelFormat: Text(s, "pix_fmt"),
            Rotation: NormalizeRotation(rotation),
            DurationSeconds: Number(s, "duration"),
            SampleRate: Number(s, "sample_rate") is { } rate ? (int)rate : null,
            Channels: Number(s, "channels") is { } channels ? (int)channels : null,
            IsAttachedPicture: attachedPicture);
    }

    /// <summary>0, 90, 180 or 270 (clockwise or counter-clockwise does not matter for display size).</summary>
    internal static int NormalizeRotation(double degrees)
    {
        var quarterTurns = (int)Math.Round(degrees / 90.0);
        return ((quarterTurns % 4 + 4) % 4) * 90;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>ffprobe writes many numbers as strings ("5.000000") and unknowns as "N/A".</summary>
    private static double? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) && double.IsFinite(d) => d,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
            _ => null,
        };
    }
}

internal sealed record ProbeStream(
    int Index,
    string CodecType,
    string CodecName,
    int Width,
    int Height,
    string? PixelFormat,
    int Rotation,
    double? DurationSeconds,
    int? SampleRate,
    int? Channels,
    bool IsAttachedPicture)
{
    public bool IsVideo => CodecType == "video" && !IsAttachedPicture;
    public bool IsAudio => CodecType == "audio";
    private bool Transposed => Rotation is 90 or 270;

    /// <summary>Size as shown to the viewer (players apply the rotation matrix).</summary>
    public int DisplayWidth => Transposed ? Height : Width;
    public int DisplayHeight => Transposed ? Width : Height;
}
