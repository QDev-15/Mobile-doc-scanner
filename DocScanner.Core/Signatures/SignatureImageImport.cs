using ImageCoreService;

namespace DocScanner.Core.Signatures;

/// <summary>How a picked picture becomes a signature.</summary>
public enum SignatureImageMode
{
    /// <summary>Reduced to one ink colour (the colour chosen for drawing), paper cut out: the same kind of ink mask a finger-drawn
    /// signature is. The original colours are lost.</summary>
    InkColor,

    /// <summary>The picture exactly as it is: its colours, nothing cut out (the paper stays as part of the signature).</summary>
    KeepOriginal,

    /// <summary>The paper cut out (transparent) but the ink keeps the colours it has in the picture (a blue pen stays blue, a
    /// stamp stays red); cropped close around the ink.</summary>
    RemoveBackground,
}

/// <summary>
/// Turns a picked photo (a signature on paper, or a signature image already on the phone) into a signature. Mostly into the same
/// kind of ink mask <see cref="SignatureInk.Rasterize"/> produces from finger strokes (0 = paper, 255 = full ink), so it can be saved
/// and placed through the exact same <c>SignatureLibrary.Add</c> / stamp path; or, for <see cref="SignatureImageMode.KeepOriginal"/>
/// and <see cref="SignatureImageMode.RemoveBackground"/>, into a colour picture signature (<see cref="Picture"/>). Reuses Otsu
/// (<see cref="Binarizer.OtsuThreshold"/>), the same from-scratch, royalty-free threshold the rest of the app uses for
/// black-and-white pages - no new algorithm, no new licence question.
/// </summary>
public static class SignatureImageImport
{
    public static GrayImage ToMask(RgbImage source)
    {
        GrayImage gray = source.ToGray();
        int threshold = Binarizer.OtsuThreshold(gray);
        // Same convention as Binarizer.Threshold ("pixels <= threshold become ink"): at or below the threshold
        // is full ink, no softening - that is the bulk of every stroke, flat value or not. Only the transition
        // ABOVE the threshold is softened over a band, so the rim fades the way a finger-drawn stroke's
        // anti-aliased edge does, instead of leaving a jagged cutout.
        double ramp = Math.Max(8.0, threshold * 0.25);

        var mask = new GrayImage(gray.Width, gray.Height);
        for (int i = 0; i < gray.Data.Length; i++)
        {
            int v = gray.Data[i];
            double coverage = v <= threshold ? 1.0 : Math.Clamp(1.0 - (v - threshold) / ramp, 0.0, 1.0);
            mask.Data[i] = (byte)Math.Round(coverage * 255);
        }
        return mask;
    }

    /// <summary>A colour picture signature: <paramref name="Colors"/> and <paramref name="Alpha"/> (255 = opaque) of the same size.</summary>
    public sealed record Picture(RgbImage Colors, GrayImage Alpha);

    /// <summary>The colour picture for <see cref="SignatureImageMode.KeepOriginal"/> / <see cref="SignatureImageMode.RemoveBackground"/>.</summary>
    public static Picture ToPicture(RgbImage source, SignatureImageMode mode)
    {
        if (mode == SignatureImageMode.InkColor) throw new ArgumentException("InkColor is a mask, see ToMask.", nameof(mode));
        if (mode == SignatureImageMode.KeepOriginal)
        {
            var opaque = new GrayImage(source.Width, source.Height);
            Array.Fill(opaque.Data, (byte)255);
            return new Picture(source, opaque);
        }
        return CutOutBackground(source);
    }

    /// <summary>Paper becomes transparent, ink keeps its own colour. The coverage of every pixel (how much of it is ink rather than paper)
    /// is the Otsu mask; the ink colour is then recovered by removing the paper's share from the pixel, so a thin pen stroke whose edge
    /// pixels are half paper still comes out in the pen's colour instead of a washed-out one.</summary>
    private static Picture CutOutBackground(RgbImage source)
    {
        GrayImage coverage = ToMask(source);
        int count = coverage.Data.Length;

        // Paper colour: the average of the pixels that are certainly paper (no ink at all); white when there are none.
        long pr = 0, pg = 0, pb = 0, pn = 0;
        for (int i = 0; i < count; i++)
        {
            if (coverage.Data[i] != 0) continue;
            pr += source.Data[i * 3];
            pg += source.Data[i * 3 + 1];
            pb += source.Data[i * 3 + 2];
            pn++;
        }
        double paperR = pn > 0 ? (double)pr / pn : 255, paperG = pn > 0 ? (double)pg / pn : 255, paperB = pn > 0 ? (double)pb / pn : 255;

        var colors = new RgbImage(source.Width, source.Height);
        for (int i = 0; i < count; i++)
        {
            double a = coverage.Data[i] / 255.0;
            int o = i * 3;
            if (a < 0.15)
            {
                // Too little ink to separate it from the paper reliably (it would only amplify noise): keep the pixel (it is nearly
                // transparent anyway).
                colors.Data[o] = source.Data[o];
                colors.Data[o + 1] = source.Data[o + 1];
                colors.Data[o + 2] = source.Data[o + 2];
                continue;
            }
            colors.Data[o] = Unmix(source.Data[o], paperR, a);
            colors.Data[o + 1] = Unmix(source.Data[o + 1], paperG, a);
            colors.Data[o + 2] = Unmix(source.Data[o + 2], paperB, a);
        }
        return Crop(new Picture(colors, coverage), margin: 0.04);
    }

    /// <summary>pixel = ink * a + paper * (1 - a)  =>  ink = (pixel - paper * (1 - a)) / a.</summary>
    private static byte Unmix(byte pixel, double paper, double a) =>
        (byte)Math.Clamp(Math.Round((pixel - paper * (1 - a)) / a), 0, 255);

    /// <summary>Crops to the visible part (alpha above a small floor) plus <paramref name="margin"/> of its longer side on every side
    /// (at least 2 pixels), like a drawn signature is cropped to its ink. An empty picture (nothing visible) is returned unchanged.</summary>
    internal static Picture Crop(Picture p, double margin)
    {
        int w = p.Alpha.Width, h = p.Alpha.Height;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (p.Alpha.Data[y * w + x] < 24) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return p;
        int pad = Math.Max(2, (int)Math.Round(Math.Max(maxX - minX + 1, maxY - minY + 1) * margin));
        int x0 = Math.Max(0, minX - pad), y0 = Math.Max(0, minY - pad);
        int x1 = Math.Min(w - 1, maxX + pad), y1 = Math.Min(h - 1, maxY + pad);
        int nw = x1 - x0 + 1, nh = y1 - y0 + 1;
        if (nw == w && nh == h) return p;

        var colors = new RgbImage(nw, nh);
        var alpha = new GrayImage(nw, nh);
        for (int y = 0; y < nh; y++)
        {
            Buffer.BlockCopy(p.Colors.Data, ((y0 + y) * w + x0) * 3, colors.Data, y * nw * 3, nw * 3);
            Buffer.BlockCopy(p.Alpha.Data, (y0 + y) * w + x0, alpha.Data, y * nw, nw);
        }
        return new Picture(colors, alpha);
    }
}
