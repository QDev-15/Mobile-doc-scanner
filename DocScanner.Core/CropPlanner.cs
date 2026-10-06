using ImageCoreService;

namespace DocScanner.Core;

/// <summary>Shape of the straightened page.</summary>
public enum CropAspect
{
    /// <summary>An A4 sheet (1 : sqrt 2), portrait or landscape following the outline. The default: a scanner
    /// produces pages of a standard size, whatever the perspective did to the photographed outline.</summary>
    A4,

    /// <summary>The proportions of the outline itself (receipts, cards, other paper sizes).</summary>
    Free,
}

/// <summary>What to decode from the original and how big the straightened page will be.</summary>
/// <param name="RegionX">Region of the stored (unrotated) original to decode, in original pixels.</param>
/// <param name="Sample">Power-of-two shrink applied while decoding (1 = full resolution).</param>
/// <param name="OutWidth">Size of the straightened page.</param>
public sealed record CropPlan(int RegionX, int RegionY, int RegionWidth, int RegionHeight, int Sample, int OutWidth, int OutHeight);

/// <summary>
/// Chooses the cheapest decode that still gives the wanted output resolution: the straightened
/// page is capped at about A4 / 300 DPI, so a 48 MP photo of a page is decoded at half or a
/// quarter scale, and only the part of the photo that holds the page is decoded at all.
/// </summary>
public static class CropPlanner
{
    /// <summary>Long edge of an A4 sheet at 300 DPI.</summary>
    public const int MaxLongEdge = 3508;

    /// <summary>Pixel budget of the straightened page (an A4 sheet at 300 DPI).</summary>
    public const long MaxPixels = 8_700_000;

    /// <summary>Pixel budget of the decoded region (16 MP = 64 MB of ARGB): what a phone can hold alongside the result.</summary>
    public const long MaxDecodePixels = 16_000_000;

    private const int RegionMargin = 2;

    /// <summary>Long edge of the result screen's live preview: sharp on a phone showing the whole page, small enough
    /// (about 2 MP) to re-filter in a fraction of a second.</summary>
    public const int PreviewLongEdge = 1800;

    /// <param name="storedPx">The outline in pixels of the stored (unrotated) original: TL, TR, BR, BL of the upright page.</param>
    /// <param name="maxLongEdge">Cap of the page's long edge; smaller for a screen preview (see <see cref="PreviewLongEdge"/>).</param>
    /// <param name="bends">Curved sides (they can bulge beyond the corners' outline: the decoded region covers them).</param>
    public static CropPlan Plan(Quad storedPx, int rawWidth, int rawHeight, CropAspect aspect = CropAspect.A4,
        int maxLongEdge = MaxLongEdge, PageBends? bends = null)
    {
        long maxPixels = maxLongEdge >= MaxLongEdge ? MaxPixels : (long)maxLongEdge * maxLongEdge; // the A4 budget scales with it
        maxLongEdge = Math.Min(maxLongEdge, MaxLongEdge);
        // The sheet's true proportions, measured through the perspective (PageGeometry): in A4 mode a real A4 sheet
        // becomes exactly A4, anything else (a letter page, a receipt, a sheet cut by the photo frame) keeps its own
        // shape instead of being stretched. The PDF export puts such a page on an A4 sheet with white margins.
        double shape = PageGeometry.OutputAspect(storedPx, rawWidth, rawHeight, aspect == CropAspect.A4);
        bool a4 = PageGeometry.IsA4(shape);

        // The outline's own size in original pixels (what the photo can resolve) ...
        (int fullW, int fullH) = PageGeometry.SizeFor(storedPx, shape, int.MaxValue, long.MaxValue);
        // ... and the page we want, capped at A4 / 300 DPI.
        (int cappedW, int cappedH) = PageGeometry.SizeFor(storedPx, shape, maxLongEdge, maxPixels);
        // Fraction of the original resolution the output needs along its more demanding axis (never above 1:
        // a photo cannot resolve more than it has).
        double wanted = Math.Min(1.0, Math.Max((double)cappedW / fullW, (double)cappedH / fullH));

        // Region: the corners, and where the sides bulge out (a bent page) the curve too.
        PointD[] p = bends is { IsFlat: false }
            ? Enumerable.Range(0, 4).SelectMany(side => Enumerable.Range(0, 17).Select(i => bends.PointOnSide(storedPx, side, i / 16.0))).ToArray()
            : storedPx.ToArray();
        int x0 = Math.Max(0, (int)Math.Floor(p.Min(v => v.X)) - RegionMargin);
        int y0 = Math.Max(0, (int)Math.Floor(p.Min(v => v.Y)) - RegionMargin);
        int x1 = Math.Min(rawWidth, (int)Math.Ceiling(p.Max(v => v.X)) + RegionMargin);
        int y1 = Math.Min(rawHeight, (int)Math.Ceiling(p.Max(v => v.Y)) + RegionMargin);
        int rw = Math.Max(1, x1 - x0), rh = Math.Max(1, y1 - y0);

        // Largest power of two that still leaves at least the resolution the output needs ...
        int sample = 1;
        while (1.0 / (sample * 2) >= wanted) sample *= 2;
        // ... but the decoded region has to fit in memory, even at the price of a smaller output.
        while ((double)rw / sample * ((double)rh / sample) > MaxDecodePixels) sample *= 2;

        // If memory forced a coarser decode than the output wanted, the output shrinks with it (same shape).
        double k = Math.Min(1.0, (1.0 / sample) / wanted);
        int outW, outH;
        if (a4)
        {
            int longSide = Math.Max(23, (int)Math.Round(Math.Max(cappedW, cappedH) * k));
            int shortSide = Math.Max(16, (int)Math.Round(longSide / PerspectiveWarp.A4Ratio));
            (outW, outH) = cappedW > cappedH ? (longSide, shortSide) : (shortSide, longSide);
        }
        else
        {
            outW = Math.Max(16, (int)Math.Round(cappedW * k));
            outH = Math.Max(16, (int)Math.Round(cappedH * k));
        }
        return new CropPlan(x0, y0, rw, rh, sample, outW, outH);
    }
}
