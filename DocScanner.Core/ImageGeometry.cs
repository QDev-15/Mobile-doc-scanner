using ImageCoreService;

namespace DocScanner.Core;

public static class ImageGeometry
{
    /// <summary>EXIF orientations 5..8 swap width and height (a 90 / 270 degree turn).</summary>
    public static bool IsTransposed(int exifOrientation) => exifOrientation is >= 5 and <= 8;

    public static (int Width, int Height) UprightSize(int rawWidth, int rawHeight, int exifOrientation) =>
        IsTransposed(exifOrientation) ? (rawHeight, rawWidth) : (rawWidth, rawHeight);

    /// <summary>Largest size with the same aspect ratio whose long edge is at most
    /// <paramref name="maxEdge"/>. Never upscales.</summary>
    public static (int Width, int Height) FitLongEdge(int width, int height, int maxEdge)
    {
        int longEdge = Math.Max(width, height);
        if (longEdge <= maxEdge || longEdge == 0) return (width, height);
        double scale = (double)maxEdge / longEdge;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>Largest power-of-two decode sample size that still leaves the long edge at
    /// or above <paramref name="targetEdge"/> (BitmapFactory.inSampleSize semantics): the
    /// decoder does the cheap integer shrink, we finish with a proper resample.</summary>
    public static int DecodeSampleSize(int width, int height, int targetEdge)
    {
        int longEdge = Math.Max(width, height);
        int sample = 1;
        while (longEdge / (sample * 2) >= targetEdge) sample *= 2;
        return sample;
    }

    // Each EXIF orientation as a 2x2 matrix [[A,B],[C,D]] acting on centred image coordinates
    // (x right, y down): the displayed position of a stored point.
    private static readonly (int A, int B, int C, int D)[] Orientations =
    [
        (1, 0, 0, 1),    // unused slot 0
        (1, 0, 0, 1),    // 1 as stored
        (-1, 0, 0, 1),   // 2 mirrored horizontally
        (-1, 0, 0, -1),  // 3 rotated 180
        (1, 0, 0, -1),   // 4 mirrored vertically
        (0, 1, 1, 0),    // 5 transposed
        (0, -1, 1, 0),   // 6 rotated 90 clockwise
        (0, -1, -1, 0),  // 7 transversed
        (0, 1, -1, 0),   // 8 rotated 90 counter-clockwise
    ];

    /// <summary>The EXIF orientation that results from turning an image with orientation
    /// <paramref name="exifOrientation"/> a further <paramref name="clockwiseDegrees"/> (a multiple of 90)
    /// clockwise on screen. Lets a user rotation be expressed with the same 1..8 codes as the file's tag.</summary>
    public static int ComposeRotation(int exifOrientation, int clockwiseDegrees)
    {
        int turns = (((clockwiseDegrees / 90) % 4) + 4) % 4;
        if (exifOrientation is < 1 or > 8) exifOrientation = 1;
        (int a, int b, int c, int d) = Orientations[exifOrientation];
        for (int i = 0; i < turns; i++)
        {
            // rotate 90 clockwise = [[0,-1],[1,0]], applied after the current orientation
            (a, b, c, d) = (-c, -d, a, b);
        }
        for (int o = 1; o <= 8; o++)
            if (Orientations[o] == (a, b, c, d)) return o;
        return 1;
    }

    /// <summary>Maps an outline drawn on the UPRIGHT picture (normalized 0..1, TL TR BR BL) back onto the
    /// stored file, whose pixels are not yet rotated by <paramref name="exifOrientation"/> (the page's
    /// effective orientation). The corner order is kept, so a warp from the stored pixels into a rectangle
    /// comes out upright and unmirrored.</summary>
    public static Quad UprightToStored(Quad q, int exifOrientation)
    {
        if (exifOrientation is < 1 or > 8) exifOrientation = 1;
        // Orientation matrices are orthogonal, so the inverse is the transpose.
        (int a, int b, int c, int d) = Orientations[exifOrientation];
        PointD Map(PointD p)
        {
            double u = p.X - 0.5, v = p.Y - 0.5;
            return new PointD(a * u + c * v + 0.5, b * u + d * v + 0.5);
        }
        return new Quad(Map(q.TopLeft), Map(q.TopRight), Map(q.BottomRight), Map(q.BottomLeft));
    }

    /// <summary>A normalized (0..1) outline after the image is turned 90 degrees clockwise. Corners are
    /// renamed so the result is again TL, TR, BR, BL (the old bottom-left corner becomes the top-left).</summary>
    public static Quad RotateQuadClockwise(Quad q)
    {
        static PointD R(PointD p) => new(1 - p.Y, p.X);
        return new Quad(R(q.BottomLeft), R(q.TopLeft), R(q.TopRight), R(q.BottomRight));
    }
}
