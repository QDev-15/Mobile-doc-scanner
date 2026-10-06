using ImageCoreService;

namespace DocScanner.Core.Signatures;

/// <summary>
/// Turns a signature drawn with a finger (strokes of points, in screen units) into an ink mask: 0 = paper, 255 = full
/// ink, anti-aliased, cropped to the ink with a small margin and scaled so its long edge is at most a given size.
/// A stroke is a chain of round-capped segments of the pen width; each pixel takes its coverage from its distance to
/// the nearest segment. Pure managed code (no platform drawing), so the same mask is made on any device and in tests.
/// </summary>
public static class SignatureInk
{
    /// <summary>Long edge of a stored signature: sharp when it is printed a third of a page wide at 300 DPI.</summary>
    public const int DefaultMaxEdge = 1200;

    /// <returns>Null when there is no ink (no stroke, or all strokes empty).</returns>
    public static GrayImage? Rasterize(IReadOnlyList<IReadOnlyList<PointD>> strokes, double penWidth, int maxEdge = DefaultMaxEdge)
    {
        var points = strokes.SelectMany(s => s).ToList();
        if (points.Count == 0 || penWidth <= 0) return null;
        double margin = penWidth; // room for the round caps and the anti-aliased rim
        double minX = points.Min(p => p.X) - margin, minY = points.Min(p => p.Y) - margin;
        double maxX = points.Max(p => p.X) + margin, maxY = points.Max(p => p.Y) + margin;
        double scale = Math.Min(1.0 * maxEdge / Math.Max(maxX - minX, maxY - minY), 4.0); // screen units -> mask pixels
        int w = Math.Max(1, (int)Math.Ceiling((maxX - minX) * scale)), h = Math.Max(1, (int)Math.Ceiling((maxY - minY) * scale));
        var mask = new GrayImage(w, h);
        double radius = penWidth * scale / 2;

        foreach (IReadOnlyList<PointD> stroke in strokes)
        {
            if (stroke.Count == 0) continue;
            PointD Map(PointD p) => new((p.X - minX) * scale, (p.Y - minY) * scale);
            PointD prev = Map(stroke[0]);
            if (stroke.Count == 1) Segment(mask, prev, prev, radius); // a dot
            for (int i = 1; i < stroke.Count; i++)
            {
                PointD next = Map(stroke[i]);
                Segment(mask, prev, next, radius);
                prev = next;
            }
        }
        return mask;
    }

    /// <summary>Paints a round-capped segment (coverage = 1 inside the pen, falling off over one pixel at its rim),
    /// keeping the darker of what is there and the new coverage.</summary>
    private static void Segment(GrayImage mask, PointD a, PointD b, double r)
    {
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X) - r - 1)), x1 = Math.Min(mask.Width - 1, (int)Math.Ceiling(Math.Max(a.X, b.X) + r + 1));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, b.Y) - r - 1)), y1 = Math.Min(mask.Height - 1, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + r + 1));
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5 - a.X, py = y + 0.5 - a.Y;
                double t = len2 == 0 ? 0 : Math.Clamp((px * dx + py * dy) / len2, 0, 1);
                double ex = px - t * dx, ey = py - t * dy;
                double coverage = Math.Clamp(r + 0.5 - Math.Sqrt(ex * ex + ey * ey), 0, 1);
                if (coverage <= 0) continue;
                byte v = (byte)Math.Round(coverage * 255);
                int i = y * mask.Width + x;
                if (v > mask.Data[i]) mask.Data[i] = v;
            }
        }
    }
}
