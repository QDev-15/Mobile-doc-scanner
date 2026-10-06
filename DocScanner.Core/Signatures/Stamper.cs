using ImageCoreService;

namespace DocScanner.Core.Signatures;

/// <summary>An ink mask (0 = paper, 255 = ink) and its color (0xRRGGBB).</summary>
public sealed record SignatureInkImage(GrayImage Mask, uint Color);

/// <summary>
/// Draws the signatures of a page (<see cref="PageStamp"/>) into the finished page: blended in their ink color on color
/// pages, in their ink's gray on gray / anti-aliased black-and-white pages, as black on 1-bit pages. The mask is sampled
/// bilinearly, turned by the stamp's quarter turns, at whatever size the stamp has on this page.
/// </summary>
public static class Stamper
{
    /// <summary>Where a stamp lands on a page of <paramref name="pageWidth"/> x <paramref name="pageHeight"/>: its
    /// footprint (after the turn) in page pixels.</summary>
    public static (double Left, double Top, double Width, double Height) Footprint(PageStamp s, int maskWidth, int maskHeight,
        int pageWidth, int pageHeight)
    {
        double w = s.Size * Math.Min(pageWidth, pageHeight);
        double h = w * maskHeight / Math.Max(1, maskWidth);
        if (s.Turns % 2 == 1) (w, h) = (h, w);
        return (s.CenterX * pageWidth - w / 2, s.CenterY * pageHeight - h / 2, w, h);
    }

    public static void Apply(RgbImage page, IReadOnlyList<PageStamp>? stamps, Func<string, SignatureInkImage?> ink)
    {
        if (stamps == null) return;
        byte[] d = page.Data;
        foreach (PageStamp s in stamps)
        {
            if (ink(s.SignatureId) is not { } sig) continue;
            byte r = (byte)(sig.Color >> 16), g = (byte)(sig.Color >> 8), b = (byte)sig.Color;
            ForEachPixel(s, sig.Mask, page.Width, page.Height, (i, alpha) =>
            {
                int o = i * 3;
                d[o] = Blend(d[o], r, alpha);
                d[o + 1] = Blend(d[o + 1], g, alpha);
                d[o + 2] = Blend(d[o + 2], b, alpha);
            });
        }
    }

    /// <param name="bilevel">The page holds only 0 and 255 (1-bit PNG): ink is black where it covers half a pixel.</param>
    public static void Apply(GrayImage page, IReadOnlyList<PageStamp>? stamps, Func<string, SignatureInkImage?> ink, bool bilevel)
    {
        if (stamps == null) return;
        byte[] d = page.Data;
        foreach (PageStamp s in stamps)
        {
            if (ink(s.SignatureId) is not { } sig) continue;
            byte level = Luma(sig.Color);
            ForEachPixel(s, sig.Mask, page.Width, page.Height, (i, alpha) =>
            {
                if (!bilevel) d[i] = Math.Min(d[i], Blend(d[i], level, alpha));
                else if (alpha >= 0.5f) d[i] = 0;
            });
        }
    }

    public static byte Luma(uint rgb) => (byte)(((rgb >> 16 & 0xFF) * 77 + (rgb >> 8 & 0xFF) * 150 + (rgb & 0xFF) * 29) >> 8);

    private static byte Blend(byte under, byte ink, float alpha) => (byte)(under + (ink - under) * alpha + 0.5f);

    private static void ForEachPixel(PageStamp s, GrayImage mask, int pageW, int pageH, Action<int, float> put)
    {
        (double left, double top, double fw, double fh) = Footprint(s, mask.Width, mask.Height, pageW, pageH);
        if (fw < 1 || fh < 1) return;
        int turns = ((s.Turns % 4) + 4) % 4;
        // Mask pixels per page pixel.
        double k = (turns % 2 == 0 ? mask.Width : mask.Height) / fw;
        int x0 = Math.Max(0, (int)Math.Floor(left)), x1 = Math.Min(pageW - 1, (int)Math.Ceiling(left + fw));
        int y0 = Math.Max(0, (int)Math.Floor(top)), y1 = Math.Min(pageH - 1, (int)Math.Ceiling(top + fh));
        int mw = mask.Width, mh = mask.Height;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                // Position in the turned signature, then back to the mask's own frame.
                double a = (x + 0.5 - left) * k, b = (y + 0.5 - top) * k;
                (double u, double v) = turns switch
                {
                    0 => (a, b),
                    1 => (b, mh - a),
                    2 => (mw - a, mh - b),
                    _ => (mw - b, a),
                };
                float alpha = Sample(mask, u - 0.5, v - 0.5) / 255f;
                if (alpha > 0.004f) put(y * pageW + x, alpha);
            }
        }
    }

    /// <summary>Bilinear sample, 0 outside the mask.</summary>
    private static float Sample(GrayImage m, double x, double y)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;
        float At(int px, int py) => (uint)px < (uint)m.Width && (uint)py < (uint)m.Height ? m.Data[py * m.Width + px] : 0;
        return (float)((At(ix, iy) * (1 - fx) + At(ix + 1, iy) * fx) * (1 - fy) + (At(ix, iy + 1) * (1 - fx) + At(ix + 1, iy + 1) * fx) * fy);
    }
}
