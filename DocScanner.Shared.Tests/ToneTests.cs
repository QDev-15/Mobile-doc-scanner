using ImageCoreService;

namespace ImageCore.Shared.Tests;

/// <summary>Brightness / contrast: one linear map, the same numbers for the live GPU preview (color matrix) and for the
/// saved page (look-up table), and it only touches color and gray pages.</summary>
public class ToneTests
{
    [Fact]
    public void Neutral_changes_nothing()
    {
        Assert.True(ToneAdjust.None.IsNeutral);
        Assert.Equal(Enumerable.Range(0, 256).Select(v => (byte)v), ToneAdjust.None.Lut());
        var img = new RgbImage(4, 4);
        new Random(1).NextBytes(img.Data);
        byte[] before = (byte[])img.Data.Clone();
        ToneAdjust.None.Apply(img);
        Assert.Equal(before, img.Data);
    }

    [Theory]
    [InlineData(40, 0)]
    [InlineData(-30, 0)]
    [InlineData(0, 50)]
    [InlineData(0, -60)]
    [InlineData(25, 35)]
    [InlineData(100, 100)]
    [InlineData(-100, -100)]
    public void The_lookup_table_is_the_color_matrix_rounded_and_clamped(int brightness, int contrast)
    {
        var tone = new ToneAdjust(brightness, contrast);
        float[] m = tone.ColorMatrix();
        byte[] lut = tone.Lut();
        // Android applies out = in * m[0] + m[4] per channel (translation in 0..255 units).
        Assert.Equal(tone.Scale, m[0]);
        Assert.Equal(tone.Scale, m[6]);
        Assert.Equal(tone.Scale, m[12]);
        Assert.Equal(1f, m[18]);
        Assert.Equal(tone.Offset, m[4]);
        for (int v = 0; v < 256; v++)
            Assert.Equal(Math.Clamp((int)MathF.Round(v * m[0] + m[4]), 0, 255), lut[v]);
    }

    [Fact]
    public void Brightness_shifts_levels_and_contrast_pivots_on_mid_gray()
    {
        Assert.Equal(128 + 51, new ToneAdjust(40, 0).Lut()[128]);   // +40 -> +51 levels
        Assert.Equal(128, new ToneAdjust(0, 80).Lut()[128]);        // contrast keeps mid-gray
        byte[] strong = new ToneAdjust(0, 70).Lut();                 // x2 around 128
        Assert.Equal(128 - 40, strong[108]);
        Assert.Equal(128 + 40, strong[148]);
        byte[] weak = new ToneAdjust(0, -70).Lut();                  // x0.5 around 128
        Assert.Equal(128 - 10, weak[108]);
        Assert.Equal(255, new ToneAdjust(100, 100).Lut()[200]);      // clamped
    }

    [Fact]
    public void Color_and_gray_pages_get_the_tone_black_and_white_ignores_it()
    {
        var page = new RgbImage(40, 30);
        Array.Fill(page.Data, (byte)100);
        var tone = new ToneAdjust(30, 0);
        byte expected = tone.Lut()[100];

        FilteredPage color = DocumentFilter.Apply(Clone(page), new FilterOptions(PageColorMode.Color) { Tone = tone }, 150);
        Assert.All(color.Color!.Data, v => Assert.Equal(expected, v));

        FilteredPage gray = DocumentFilter.Apply(Clone(page), new FilterOptions(PageColorMode.Gray, CleanBackground: false) { Tone = tone }, 150);
        Assert.All(gray.Gray!.Data, v => Assert.Equal(expected, v));

        FilteredPage bwPlain = DocumentFilter.Apply(Clone(page), new FilterOptions(PageColorMode.BlackWhite), 150);
        FilteredPage bwTone = DocumentFilter.Apply(Clone(page), new FilterOptions(PageColorMode.BlackWhite) { Tone = tone }, 150);
        Assert.Equal(bwPlain.Gray!.Data, bwTone.Gray!.Data);
    }

    [Fact]
    public void Same_look_counts_only_the_settings_that_change_the_picture()
    {
        var tone = new ToneAdjust(10, 0);
        FilterOptions Color(ToneAdjust t, bool clean = true, int dark = 50) => new(PageColorMode.Color, dark, clean) { Tone = t };
        FilterOptions Gray(ToneAdjust t, bool clean = true, int dark = 50) => new(PageColorMode.Gray, dark, clean) { Tone = t };
        FilterOptions Bw(ToneAdjust t, bool clean = true, int dark = 50) => new(PageColorMode.BlackWhite, dark, clean) { Tone = t };

        Assert.False(FilterOptions.SameLook(Color(ToneAdjust.None), Color(tone)));
        Assert.True(FilterOptions.SameLook(Color(tone, clean: false, dark: 5), Color(tone)));   // color: cleaning, darkness irrelevant
        Assert.False(FilterOptions.SameLook(Gray(ToneAdjust.None), Gray(tone)));
        Assert.False(FilterOptions.SameLook(Gray(tone, clean: false), Gray(tone)));
        Assert.False(FilterOptions.SameLook(Bw(ToneAdjust.None), Bw(tone)));                     // black and white: brightness moves the threshold
        Assert.True(FilterOptions.SameLook(Bw(new ToneAdjust(10, 0)), Bw(new ToneAdjust(10, 40)))); // ... contrast is not used
        Assert.False(FilterOptions.SameLook(Bw(tone, dark: 60), Bw(tone)));
        Assert.False(FilterOptions.SameLook(Color(tone), Gray(tone)));
    }

    private static RgbImage Clone(RgbImage src) => new(src.Width, src.Height, (byte[])src.Data.Clone());
}

/// <summary>Quarter turns of a page (result screen's rotate buttons).</summary>
public class RotateTests
{
    private static RgbImage Numbered(int w, int h)
    {
        var img = new RgbImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                img.Data[o] = (byte)x;
                img.Data[o + 1] = (byte)y;
                img.Data[o + 2] = 7;
            }
        return img;
    }

    private static (int X, int Y) At(RgbImage img, int x, int y)
    {
        int o = (y * img.Width + x) * 3;
        return (img.Data[o], img.Data[o + 1]);
    }

    [Fact]
    public void A_clockwise_turn_puts_the_left_edge_on_top()
    {
        RgbImage src = Numbered(5, 3);
        RgbImage cw = src.RotateClockwise(1);
        Assert.Equal((3, 5), (cw.Width, cw.Height));
        Assert.Equal((0, 2), At(cw, 0, 0));   // bottom-left of the source is now top-left
        Assert.Equal((0, 0), At(cw, 2, 0));   // top-left is now top-right
        Assert.Equal((4, 0), At(cw, 2, 4));   // top-right is now bottom-right

        RgbImage ccw = src.RotateClockwise(-1);
        Assert.Equal((3, 5), (ccw.Width, ccw.Height));
        Assert.Equal((4, 0), At(ccw, 0, 0));  // top-right is now top-left

        RgbImage half = src.RotateClockwise(2);
        Assert.Equal((5, 3), (half.Width, half.Height));
        Assert.Equal((4, 2), At(half, 0, 0));
    }

    [Fact]
    public void Turns_add_up()
    {
        RgbImage src = Numbered(7, 4);
        Assert.Same(src, src.RotateClockwise(0));
        Assert.Equal(src.Data, src.RotateClockwise(1).RotateClockwise(3).Data);
        Assert.Equal(src.Data, src.RotateClockwise(4).Data);
        Assert.Equal(src.RotateClockwise(2).Data, src.RotateClockwise(1).RotateClockwise(1).Data);
        Assert.Equal(src.RotateClockwise(-1).Data, src.RotateClockwise(3).Data);
    }
}

/// <summary>Sauvola split into statistics + threshold (the result screen's live darkness / brightness), with a brightness offset.</summary>
public class SauvolaStatsTests
{
    private static GrayImage Page(int w, int h, int seed, int ink = 40)
    {
        var rnd = new Random(seed);
        var img = new GrayImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // paper with a shadow gradient, text-like dark bars, noise
                int v = 220 - x * 60 / w;
                if ((y / 6) % 3 == 0 && (x / 9) % 4 != 3) v = ink + rnd.Next(30);
                img[x, y] = (byte)Math.Clamp(v + rnd.Next(-8, 9), 0, 255);
            }
        return img;
    }

    [Fact]
    public void Offset_zero_is_plain_sauvola_bit_for_bit()
    {
        GrayImage src = Page(311, 207, 1);
        Assert.Equal(Binarizer.Sauvola(src, 31, 0.34).Data, Binarizer.Sauvola(src, 31, 0.34, 0).Data);
    }

    [Theory]
    [InlineData(0.34, 0)]
    [InlineData(0.2, 25)]
    [InlineData(0.5, -40)]
    public void Stats_then_threshold_matches_sauvola(double k, double offset)
    {
        GrayImage src = Page(400, 300, 2);
        GrayImage direct = Binarizer.Sauvola(src, 31, k, offset);
        SauvolaStats stats = Binarizer.Stats(src, 31);
        var fast = new GrayImage(src.Width, src.Height);
        Binarizer.Threshold(src, stats, k, offset, fast);
        int differ = direct.Data.Zip(fast.Data).Count(p => p.First != p.Second);
        Assert.True(differ <= src.Data.Length / 2000, $"{differ} pixels differ"); // float statistics: rare ties at the threshold
    }

    [Fact]
    public void A_brighter_page_has_less_ink()
    {
        GrayImage src = Page(400, 300, 3, ink: 140); // faint strokes (pencil): the ones brightness decides about
        int Ink(double offset) => Binarizer.Sauvola(src, 31, 0.34, offset).Data.Count(v => v == 0);
        Assert.True(Ink(40) < Ink(0));
        Assert.True(Ink(-40) > Ink(0));
    }

    [Fact]
    public void Statistics_of_a_turned_image_are_the_turned_statistics()
    {
        GrayImage src = Page(157, 93, 4);
        SauvolaStats stats = Binarizer.Stats(src, 21);
        for (int turns = 1; turns <= 3; turns++)
        {
            SauvolaStats turned = stats.RotateClockwise(turns), direct = Binarizer.Stats(src.RotateClockwise(turns), 21);
            Assert.Equal((direct.Width, direct.Height), (turned.Width, turned.Height));
            Assert.Equal(direct.Mean, turned.Mean);
            Assert.Equal(direct.Deviation, turned.Deviation);
        }
    }

    [Fact]
    public void Gray_turns_match_color_turns()
    {
        var rgb = new RgbImage(7, 4);
        new Random(5).NextBytes(rgb.Data);
        GrayImage gray = rgb.ToGray();
        for (int turns = -1; turns <= 3; turns++)
            Assert.Equal(rgb.RotateClockwise(turns).ToGray().Data, gray.RotateClockwise(turns).Data);
    }
}
