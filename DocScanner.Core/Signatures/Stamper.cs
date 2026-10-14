using ImageCoreService;

namespace DocScanner.Core.Signatures;

/// <summary>
/// A signature as it is stamped: an alpha <see cref="Mask"/> (0 = transparent, 255 = opaque) plus EITHER one ink
/// <see cref="Color"/> (0xRRGGBB: a drawn signature, or a picture reduced to single-colour ink) OR, for a colour
/// picture signature, its own per-pixel <see cref="Picture"/> colours (same size as the mask) which are used as they are.
/// </summary>
public sealed record SignatureInkImage(GrayImage Mask, uint Color, RgbImage? Picture = null);

/// <summary>
/// Draws the signatures of a page (<see cref="PageStamp"/>) into the finished page: blended in their ink color on color
/// pages (or in their own picture colours for a colour picture), in their ink's gray on gray / anti-aliased black-and-white
/// pages, as black on 1-bit pages. The mask (and picture) is sampled bilinearly, turned by the stamp's quarter turns, at
/// whatever size the stamp has on this page.
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
            RgbImage? pic = sig.Picture;
            ForEachPixel(s, sig.Mask, page.Width, page.Height, (i, alpha, u, v) =>
            {
                int o = i * 3;
                if (pic != null)
                {
                    SampleColor(pic, sig.Mask, u - 0.5, v - 0.5, out byte pr, out byte pg, out byte pb);
                    d[o] = Blend(d[o], pr, alpha);
                    d[o + 1] = Blend(d[o + 1], pg, alpha);
                    d[o + 2] = Blend(d[o + 2], pb, alpha);
                    return;
                }
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
            RgbImage? pic = sig.Picture;
            ForEachPixel(s, sig.Mask, page.Width, page.Height, (i, alpha, u, v) =>
            {
                if (pic != null)
                {
                    // A colour picture on a gray page: its own gray level replaces the page where it is opaque (a photo,
                    // not just dark ink), so paper stays paper-white and the pen stays dark.
                    SampleColor(pic, sig.Mask, u - 0.5, v - 0.5, out byte pr, out byte pg, out byte pb);
                    byte gray = (byte)((pr * 77 + pg * 150 + pb * 29) >> 8);
                    if (!bilevel) d[i] = Blend(d[i], gray, alpha);
                    else if (alpha >= 0.5f) d[i] = gray < 128 ? (byte)0 : (byte)255;
                    return;
                }
                if (!bilevel) d[i] = Math.Min(d[i], Blend(d[i], level, alpha));
                else if (alpha >= 0.5f) d[i] = 0;
            });
        }
    }

    public static byte Luma(uint rgb) => (byte)(((rgb >> 16 & 0xFF) * 77 + (rgb >> 8 & 0xFF) * 150 + (rgb & 0xFF) * 29) >> 8);

    private static byte Blend(byte under, byte ink, float alpha) => (byte)(under + (ink - under) * alpha + 0.5f);

    /// <summary>Calls <paramref name="put"/> for every page pixel the stamp covers: page pixel index, coverage 0..1, and the position
    /// (<c>u</c>, <c>v</c>) in the signature's own pixel frame (for sampling a colour picture).</summary>
    private static void ForEachPixel(PageStamp s, GrayImage mask, int pageW, int pageH, Action<int, float, double, double> put)
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
                if (alpha > 0.004f) put(y * pageW + x, alpha, u, v);
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

    /// <summary>Bilinear colour of a picture signature at (x, y), each neighbour weighted by its own alpha so the colour of fully
    /// transparent neighbours (arbitrary) never bleeds into the edge of the visible part.</summary>
    private static void SampleColor(RgbImage pic, GrayImage alpha, double x, double y, out byte r, out byte g, out byte b)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;
        double sr = 0, sg = 0, sb = 0, sw = 0;
        for (int dy = 0; dy <= 1; dy++)
        {
            for (int dx = 0; dx <= 1; dx++)
            {
                int px = Math.Clamp(ix + dx, 0, pic.Width - 1), py = Math.Clamp(iy + dy, 0, pic.Height - 1);
                double w = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy) * (alpha.Data[py * pic.Width + px] + 1);
                int o = (py * pic.Width + px) * 3;
                sr += pic.Data[o] * w;
                sg += pic.Data[o + 1] * w;
                sb += pic.Data[o + 2] * w;
                sw += w;
            }
        }
        if (sw <= 0) { r = g = b = 0; return; }
        r = (byte)Math.Clamp(Math.Round(sr / sw), 0, 255);
        g = (byte)Math.Clamp(Math.Round(sg / sw), 0, 255);
        b = (byte)Math.Clamp(Math.Round(sb / sw), 0, 255);
    }
}
