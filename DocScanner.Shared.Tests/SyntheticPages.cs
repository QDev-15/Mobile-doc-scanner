using ImageCoreService;

namespace ImageCore.Shared.Tests;

/// <summary>Tiny page generator (no bitmaps): white paper with rows of "text" (short black
/// dashes), optionally skewed, tinted or bordered.</summary>
internal static class SyntheticPages
{
    public const int Dpi = 100;

    public static GrayImage TextPage(int w = 800, int h = 1100, double skewDeg = 0, byte paper = 255)
    {
        var img = new GrayImage(w, h);
        Array.Fill(img.Data, paper);
        double slope = Math.Tan(skewDeg * Math.PI / 180);
        for (int line = 0; line < 30; line++)
        {
            int baseY = 90 + line * 32;
            for (int x = 80; x < w - 80; x++)
            {
                // Dashes with gaps, like words. Content rotated clockwise on screen => y grows with x.
                if ((x / 14) % 5 == 4) continue;
                int y = baseY + (int)Math.Round((x - w / 2) * slope);
                for (int t = 0; t < 4; t++)
                    if ((uint)(y + t) < (uint)h) img[x, y + t] = 30;
            }
        }
        return img;
    }

    public static GrayImage WithBlackBorder(GrayImage page, int border)
    {
        int w = page.Width + 2 * border, h = page.Height + 2 * border;
        var img = new GrayImage(w, h); // zero-filled = black
        for (int y = 0; y < page.Height; y++)
            Array.Copy(page.Data, y * page.Width, img.Data, (y + border) * w + border, page.Width);
        return img;
    }
}
