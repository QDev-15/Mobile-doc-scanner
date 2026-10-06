using ImageCoreService;

namespace DocScanner.Core.Signatures;

/// <summary>
/// Turns a picked photo (a signature on paper, or a signature image already on the phone) into the same kind
/// of ink mask <see cref="SignatureInk.Rasterize"/> produces from finger strokes (0 = paper, 255 = full ink),
/// so it can be saved and placed through the exact same <c>SignatureLibrary.Add</c> / stamp path. Reuses
/// Otsu (<see cref="Binarizer.OtsuThreshold"/>), the same from-scratch, royalty-free threshold the rest of the
/// app uses for black-and-white pages - no new algorithm, no new licence question.
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
}
