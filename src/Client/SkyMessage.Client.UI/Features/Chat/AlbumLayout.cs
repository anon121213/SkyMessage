using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Chat;

/// <summary>One tile of an album row: index into the attachments and its (clamped) aspect ratio.</summary>
public sealed record AlbumTile(int Index, double Aspect);

/// <summary>A row of 1–3 tiles. <see cref="Aspect"/> = row width / row height, so the row fills the album width.</summary>
public sealed record AlbumRow(IReadOnlyList<AlbumTile> Tiles, double Aspect);

/// <summary>
/// Justified album layout: items keep their order and are split into rows of 1–3 tiles whose heights match within a
/// row (widths proportional to the aspect ratios). The split minimizes how far each row's shape is from a comfortable
/// strip (dynamic programming over at most 10 items), discouraging lone narrow tiles.
/// </summary>
public static class AlbumLayout
{
    /// <summary>Row aspect (width/height) that reads best in a chat bubble.</summary>
    private const double IdealRowAspect = 2.2;
    private const double MinAspect = 0.5;
    private const double MaxAspect = 2.4;

    public static double AspectOf(MediaDto media) => AspectOf(media.Width, media.Height);

    public static double AspectOf(int? width, int? height) =>
        width is > 0 && height is > 0 ? (double)width.Value / height.Value : 1;

    public static IReadOnlyList<AlbumRow> Compute(IReadOnlyList<double> aspects)
    {
        var n = aspects.Count;
        if (n == 0) return [];
        var a = aspects.Select(x => double.IsFinite(x) && x > 0 ? Math.Clamp(x, MinAspect, MaxAspect) : 1).ToArray();
        if (n == 1) return [new AlbumRow([new AlbumTile(0, a[0])], a[0])];

        var best = new double[n + 1];
        var split = new int[n + 1];
        Array.Fill(best, double.MaxValue);
        best[0] = 0;
        for (var i = 1; i <= n; i++)
        {
            for (var k = 1; k <= Math.Min(3, i); k++)
            {
                var sum = 0d;
                for (var j = i - k; j < i; j++) sum += a[j];
                var cost = best[i - k] + RowCost(sum, k);
                if (cost < best[i])
                {
                    best[i] = cost;
                    split[i] = k;
                }
            }
        }

        var rows = new List<AlbumRow>();
        for (var i = n; i > 0; i -= split[i])
        {
            var k = split[i];
            var tiles = Enumerable.Range(i - k, k).Select(j => new AlbumTile(j, a[j])).ToList();
            rows.Add(new AlbumRow(tiles, tiles.Sum(t => t.Aspect)));
        }
        rows.Reverse();
        return rows;
    }

    private static double RowCost(double rowAspect, int count)
    {
        var shape = Math.Log(rowAspect / IdealRowAspect);
        var cost = shape * shape;
        // A tile alone in its row is fine only when it is a wide picture.
        if (count == 1) cost += rowAspect >= 1.6 ? 0.15 : 0.6;
        return cost;
    }
}
