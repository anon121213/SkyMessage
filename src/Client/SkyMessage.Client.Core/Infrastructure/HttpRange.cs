using System.Globalization;

namespace SkyMessage.Client.Core.Infrastructure;

internal enum RangeOutcome
{
    /// <summary>No usable Range header: serve the whole body with 200.</summary>
    Full,
    /// <summary>One satisfiable range: serve 206.</summary>
    Partial,
    /// <summary>Syntactically valid but outside the representation: 416.</summary>
    Unsatisfiable,
}

/// <summary>Inclusive byte range.</summary>
internal readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;

    public string ContentRange(long size) => $"bytes {Start}-{End}/{size}";

    public string RangeHeader() => $"bytes={Start}-{End}";
}

/// <summary>RFC 9110 single-range parsing. Multi-range and malformed headers are ignored (a server may serve 200).</summary>
internal static class HttpRange
{
    public static RangeOutcome Parse(string? header, long size, out ByteRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(header)) return RangeOutcome.Full;

        var value = header.Trim();
        const string unit = "bytes=";
        if (!value.StartsWith(unit, StringComparison.OrdinalIgnoreCase)) return RangeOutcome.Full;
        var spec = value[unit.Length..].Trim();
        if (spec.Length == 0 || spec.Contains(',')) return RangeOutcome.Full;

        var dash = spec.IndexOf('-');
        if (dash < 0) return RangeOutcome.Full;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();

        if (first.Length == 0)
        {
            // Suffix range: the last N bytes.
            if (!TryParseNonNegative(last, out var suffix)) return RangeOutcome.Full;
            if (suffix == 0 || size == 0) return RangeOutcome.Unsatisfiable;
            range = new ByteRange(Math.Max(0, size - suffix), size - 1);
            return RangeOutcome.Partial;
        }

        if (!TryParseNonNegative(first, out var start)) return RangeOutcome.Full;
        long end;
        if (last.Length == 0)
        {
            end = size - 1;
        }
        else
        {
            if (!TryParseNonNegative(last, out end)) return RangeOutcome.Full;
            if (end < start) return RangeOutcome.Full;
            end = Math.Min(end, size - 1);
        }

        if (start >= size) return RangeOutcome.Unsatisfiable;
        range = new ByteRange(start, end);
        return RangeOutcome.Partial;
    }

    private static bool TryParseNonNegative(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
}
