using ImageCoreService;

namespace ImageCore.Shared.Tests;

/// <summary>Renders synthetic "photos of a sheet of paper on a table" with a known outline, so
/// the edge detector can be scored against ground truth.</summary>
public static class SceneBuilder
{
    private const int PaperW = 500, PaperH = 700;

    public sealed record Options(
        (byte R, byte G, byte B) Background,
        (byte R, byte G, byte B) Paper,
        bool Stripes = false,          // wood-grain-like bands in the background
        double Shadow = 0,             // brightness falloff left -> right (0..0.4)
        int Noise = 0,                 // +/- per channel
        bool Clutter = false,          // dark objects lying on the table
        int DropShadow = 0,            // soft shadow under the sheet, offset down-right by this many px
        int Seed = 1);

    public static RgbImage Render(int w, int h, Quad paperPx, Options o)
    {
        // paper (u, v) -> scene pixel, inverted to walk scene pixels back into paper space
        var paperRect = new[] { new PointD(0, 0), new PointD(PaperW, 0), new PointD(PaperW, PaperH), new PointD(0, PaperH) };
        Homography toPaper = Homography.FromPoints(paperPx.ToArray(), paperRect);
        var rnd = new Random(o.Seed);
        var img = new RgbImage(w, h);
        float[]? shadow = o.DropShadow > 0 ? ShadowMask(w, h, paperPx, o.DropShadow) : null;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double r = 0, g = 0, b = 0;
                for (int sy = 0; sy < 2; sy++)
                {
                    for (int sx = 0; sx < 2; sx++)
                    {
                        double px = x + 0.25 + sx * 0.5, py = y + 0.25 + sy * 0.5;
                        PointD uv = toPaper.Apply(px, py);
                        (double sr, double sg, double sb) = uv.X >= 0 && uv.X < PaperW && uv.Y >= 0 && uv.Y < PaperH
                            ? PaperPixel(uv.X, uv.Y, o.Paper)
                            : BackgroundPixel(px, py, w, h, o);
                        if (shadow != null && !(uv.X >= 0 && uv.X < PaperW && uv.Y >= 0 && uv.Y < PaperH))
                        {
                            double k = 1 - 0.45 * shadow[y * w + x];
                            sr *= k; sg *= k; sb *= k;
                        }
                        r += sr; g += sg; b += sb;
                    }
                }
                double light = 1 - o.Shadow * x / w;
                int i = (y * w + x) * 3;
                img.Data[i] = Clamp(r / 4 * light + Noise(rnd, o.Noise));
                img.Data[i + 1] = Clamp(g / 4 * light + Noise(rnd, o.Noise));
                img.Data[i + 2] = Clamp(b / 4 * light + Noise(rnd, o.Noise));
            }
        }
        return img;
    }

    private static (double, double, double) PaperPixel(double u, double v, (byte R, byte G, byte B) paper)
    {
        // Rows of text: 5 px tall every 28 px, words separated by gaps; 50 px margins.
        bool text = u >= 50 && u <= 450 && v >= 60 && v <= 640 && ((int)(v - 60) % 28) < 5 && ((int)u / 23) % 5 != 4;
        bool rule = u >= 50 && u <= 450 && v >= 128 && v <= 134; // the line under a heading
        return text || rule ? (45, 45, 50) : (paper.R, paper.G, paper.B);
    }

    private static (double, double, double) BackgroundPixel(double x, double y, int w, int h, Options o)
    {
        double k = 1;
        if (o.Stripes) k = 1 + 0.18 * Math.Sin(y * 0.31 + Math.Sin(x * 0.05) * 3) + 0.08 * Math.Sin(x * 0.9);
        if (o.Clutter)
        {
            // A dark phone-like slab and a pen, well away from the sheet's interior.
            if (x > w * 0.02 && x < w * 0.10 && y > h * 0.1 && y < h * 0.45) return (25, 25, 30);
            if (Math.Abs(y - (h * 0.93 - (x - w * 0.6) * 0.05)) < 4 && x > w * 0.55 && x < w * 0.9) return (20, 20, 90);
        }
        return (o.Background.R * k, o.Background.G * k, o.Background.B * k);
    }

    /// <summary>Soft shadow of the sheet: its outline shifted down-right, box-blurred.</summary>
    private static float[] ShadowMask(int w, int h, Quad paperPx, int offset)
    {
        var m = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                m[y * w + x] = paperPx.Contains(x - offset, y - offset * 1.4) ? 1f : 0f;
        for (int pass = 0; pass < 2; pass++)
        {
            int r = 6;
            var t = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0; int n = 0;
                    for (int dx = -r; dx <= r; dx++) { int xx = x + dx; if (xx < 0 || xx >= w) continue; sum += m[y * w + xx]; n++; }
                    t[y * w + x] = sum / n;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0; int n = 0;
                    for (int dy = -r; dy <= r; dy++) { int yy = y + dy; if (yy < 0 || yy >= h) continue; sum += t[yy * w + x]; n++; }
                    m[y * w + x] = sum / n;
                }
        }
        return m;
    }

    private static double Noise(Random rnd, int amp) => amp == 0 ? 0 : rnd.Next(-amp, amp + 1);

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>Corner errors in px (max over the 4 corners) and IoU, both measured against the
    /// true outline; only the visible part of the frame counts for IoU.</summary>
    public static (double MaxCornerErrorFraction, double Iou) Score(Quad truthPx, Quad detectedNormalized, int w, int h)
    {
        Quad det = detectedNormalized.Scale(w, h);
        double diag = Math.Sqrt((double)w * w + (double)h * h);
        PointD[] t = truthPx.ToArray(), d = det.ToArray();
        // The detector always starts at the corner nearest the frame's top-left, which for a
        // sheet rotated past 45 degrees is a different corner than the ground truth's "TL":
        // compare under the best of the four cyclic relabelings.
        double worst = double.MaxValue;
        for (int shift = 0; shift < 4; shift++)
        {
            double w0 = 0;
            for (int i = 0; i < 4; i++)
            {
                // A corner outside the frame cannot be seen; compare its clamped position.
                PointD tc = t[(i + shift) % 4];
                double tx = Math.Clamp(tc.X, 0, w), ty = Math.Clamp(tc.Y, 0, h);
                w0 = Math.Max(w0, Math.Sqrt((tx - d[i].X) * (tx - d[i].X) + (ty - d[i].Y) * (ty - d[i].Y)));
            }
            worst = Math.Min(worst, w0);
        }

        long inter = 0, union = 0;
        for (int y = 0; y < h; y += 4)
        {
            for (int x = 0; x < w; x += 4)
            {
                bool a = truthPx.Contains(x, y), b = det.Contains(x, y);
                if (a && b) inter++;
                if (a || b) union++;
            }
        }
        return (worst / diag, union == 0 ? 0 : (double)inter / union);
    }
}
