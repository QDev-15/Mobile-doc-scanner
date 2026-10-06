using System.IO.Compression;
using ImageCoreService;

namespace ImageCore.Shared.Tests;

public class FilterTests
{
    /// <summary>The original integral-image Sauvola (two long[] of (w+1)*(h+1)), kept here as the reference
    /// the low-memory version must match pixel for pixel.</summary>
    private static GrayImage SauvolaReference(GrayImage src, int window, double k)
    {
        int w = src.Width, h = src.Height, stride = w + 1;
        var sum = new long[(long)stride * (h + 1)];
        var sq = new long[(long)stride * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long rowSum = 0, rowSq = 0;
            for (int x = 0; x < w; x++)
            {
                int v = src.Data[y * w + x];
                rowSum += v;
                rowSq += v * v;
                sum[(y + 1) * stride + x + 1] = sum[y * stride + x + 1] + rowSum;
                sq[(y + 1) * stride + x + 1] = sq[y * stride + x + 1] + rowSq;
            }
        }
        int half = window / 2;
        var dst = new GrayImage(w, h);
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - half), y1 = Math.Min(h - 1, y + half);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - half), x1 = Math.Min(w - 1, x + half);
                long n = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                long s = sum[(y1 + 1) * stride + x1 + 1] - sum[y0 * stride + x1 + 1] - sum[(y1 + 1) * stride + x0] + sum[y0 * stride + x0];
                long s2 = sq[(y1 + 1) * stride + x1 + 1] - sq[y0 * stride + x1 + 1] - sq[(y1 + 1) * stride + x0] + sq[y0 * stride + x0];
                double mean = s / (double)n;
                double variance = Math.Max(0, s2 / (double)n - mean * mean);
                double t = mean * (1 + k * (Math.Sqrt(variance) / 128.0 - 1));
                dst.Data[y * w + x] = src.Data[y * w + x] <= t ? (byte)0 : (byte)255;
            }
        }
        return dst;
    }

    [Theory]
    [InlineData(1, 1, 15)]      // degenerate
    [InlineData(37, 23, 151)]   // window larger than the image
    [InlineData(301, 517, 37)]  // several parallel bands
    [InlineData(640, 900, 15)]
    public void Low_memory_Sauvola_matches_the_integral_image_version_exactly(int w, int h, int window)
    {
        var rnd = new Random(w * 31 + h);
        var img = new GrayImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img[x, y] = (byte)Math.Clamp(120 + 100 * Math.Sin(x / 17.0) * Math.Cos(y / 29.0) + rnd.Next(-40, 40), 0, 255);

        Assert.Equal(SauvolaReference(img, window, 0.34).Data, Binarizer.Sauvola(img, window, 0.34).Data);
    }

    [Fact]
    public void Low_memory_Sauvola_matches_on_a_text_page()
    {
        GrayImage page = SyntheticPages.TextPage(paper: 180);
        Assert.Equal(SauvolaReference(page, 37, 0.34).Data, Binarizer.Sauvola(page, 37, 0.34).Data);
    }

    /// <summary>A page lit from one side: paper from 250 down to 90, with ink.</summary>
    private static GrayImage ShadowedPage(int w = 800, int h = 1100)
    {
        GrayImage text = SyntheticPages.TextPage(w, h);
        var page = new GrayImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double light = 250 - 160.0 * x / w;               // gradient left -> right
                page[x, y] = (byte)(text[x, y] < 128 ? light * 0.25 : light);
            }
        return page;
    }

    [Fact]
    public void Background_flattening_makes_shadowed_paper_evenly_white_and_keeps_ink()
    {
        GrayImage page = ShadowedPage();
        GrayImage flat = BackgroundFlattener.Flatten(page);

        // Paper samples (text-free margin) on the bright and the dark side.
        double left = MeanOf(flat, 10, 10, 40, 40), right = MeanOf(flat, page.Width - 50, 10, 40, 40);
        Assert.True(left > 235 && right > 235, $"paper after flattening: left {left:0}, right {right:0}");
        Assert.True(MeanOf(page, page.Width - 50, 10, 40, 40) < 110, "the test page really is dark on the right");

        // Ink stays dark on both sides.
        GrayImage text = SyntheticPages.TextPage(page.Width, page.Height);
        double inkLeft = MeanWhere(flat, text, 0, page.Width / 3), inkRight = MeanWhere(flat, text, 2 * page.Width / 3, page.Width);
        Assert.True(inkLeft < 110 && inkRight < 110, $"ink after flattening: {inkLeft:0} / {inkRight:0}");
    }

    [Fact]
    public void Black_and_white_filter_turns_a_shadowed_photo_into_clean_text()
    {
        GrayImage page = ShadowedPage();
        var rgb = new RgbImage(page.Width, page.Height);
        for (int i = 0; i < page.Data.Length; i++)
            rgb.Data[i * 3] = rgb.Data[i * 3 + 1] = rgb.Data[i * 3 + 2] = page.Data[i];

        FilteredPage hard = DocumentFilter.Apply(rgb, new FilterOptions(PageColorMode.BlackWhite, Smooth: false), SyntheticPages.Dpi);
        Assert.True(hard.IsBilevel);
        Assert.All(hard.Gray!.Data, v => Assert.True(v is 0 or 255));

        // Default: anti-aliased edges, but paper and ink themselves pure white / black.
        FilteredPage bw = DocumentFilter.Apply(rgb, new FilterOptions(PageColorMode.BlackWhite), SyntheticPages.Dpi);
        Assert.True(bw.IsBlackWhite);
        Assert.False(bw.IsBilevel);
        double pure = bw.Gray!.Data.Count(v => v is 0 or 255) / (double)bw.Gray.Data.Length;
        Assert.True(pure > 0.97, $"pure black / white: {pure:P1}");

        GrayImage truth = SyntheticPages.TextPage(page.Width, page.Height);
        int wrongPaper = 0, paper = 0, keptInk = 0, ink = 0;
        for (int i = 0; i < truth.Data.Length; i++)
        {
            if (truth.Data[i] < 128) { ink++; if (bw.Gray.Data[i] < 128) keptInk++; }
            else { paper++; if (bw.Gray.Data[i] < 128) wrongPaper++; }
        }
        Assert.True(wrongPaper < paper / 200, $"paper turned black: {wrongPaper} of {paper}");
        Assert.True(keptInk > ink * 0.85, $"ink kept: {keptInk} of {ink}");
    }

    /// <summary>A soft phone photo of thin text: the plain threshold drops much of the thin strokes (diacritics, slashes),
    /// sharpening first keeps them.</summary>
    [Fact]
    public void Sharpening_keeps_the_thin_strokes_of_a_soft_photo()
    {
        // Hairlines (1 px) and 2 x 2 dots (diacritics) at 300 DPI, next to 3 px strokes.
        var truth = new GrayImage(600, 600);
        Array.Fill(truth.Data, (byte)255);
        for (int y = 40; y < 560; y += 16)
            for (int x = 40; x < 560; x++)
            {
                if ((x / 20) % 4 == 3) continue;
                truth[x, y] = 20;
                if (x % 40 == 5) { truth[x, y - 5] = truth[x + 1, y - 5] = truth[x, y - 4] = truth[x + 1, y - 4] = 20; }
                if ((x / 20) % 4 == 0) truth[x, y + 6] = truth[x, y + 7] = truth[x, y + 8] = 20;
            }
        var soft = new GrayImage(truth.Width, truth.Height);
        for (int i = 0; i < soft.Data.Length; i++) soft.Data[i] = (byte)(40 + truth.Data[i] * 0.72); // gray ink on gray paper
        Blur(soft);
        Blur(soft);
        var rgb = RgbImage.FromGray(soft);

        double Kept(bool sharpen)
        {
            GrayImage bw = DocumentFilter.Apply(rgb, new FilterOptions(PageColorMode.BlackWhite, Sharpen: sharpen), 300).Gray!;
            int ink = 0, kept = 0;
            for (int i = 0; i < truth.Data.Length; i++)
                if (truth.Data[i] < 128) { ink++; if (bw.Data[i] < 128) kept++; }
            return (double)kept / ink;
        }
        double plain = Kept(false), sharpened = Kept(true);
        Assert.True(sharpened > plain + 0.03, $"plain {plain:P1}, sharpened {sharpened:P1}");
    }

    private static void Blur(GrayImage g)
    {
        var copy = (byte[])g.Data.Clone();
        for (int y = 1; y < g.Height - 1; y++)
            for (int x = 1; x < g.Width - 1; x++)
            {
                int sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++) sum += copy[(y + dy) * g.Width + x + dx];
                g.Data[y * g.Width + x] = (byte)(sum / 9);
            }
    }

    [Fact]
    public void Gray_png_round_trips()
    {
        var img = new GrayImage(123, 77);
        new Random(3).NextBytes(img.Data);
        Assert.Equal(img.Data, PngReader.DecodeGray8(PngWriter.EncodeGray8(img)).Data);
    }

    [Fact]
    public void Smooth_black_and_white_despeckles_like_the_hard_one()
    {
        var page = new GrayImage(200, 200);
        Array.Fill(page.Data, (byte)255);
        page.Data[100 * 200 + 100] = 100; // a one-pixel speck, soft
        for (int y = 20; y < 40; y++) for (int x = 20; x < 180; x++) page.Data[y * 200 + x] = 0; // a real stroke
        DocumentFilter.Despeckle(page, 300);
        Assert.Equal(255, page.Data[100 * 200 + 100]);
        Assert.Equal(0, page.Data[30 * 200 + 100]);
    }

    [Fact]
    public void Per_channel_flattening_turns_tinted_shadowed_paper_white_and_keeps_colored_marks()
    {
        // Yellowish paper, darker to the right (shadow), black text lines and a red stamp.
        GrayImage text = SyntheticPages.TextPage(800, 1100);
        var page = new RgbImage(800, 1100);
        for (int y = 0; y < 1100; y++)
            for (int x = 0; x < 800; x++)
            {
                double light = 1.0 - 0.5 * x / 800;
                (int r, int g, int b) = text[x, y] < 128 ? (30, 30, 35) : (245, 232, 190);            // ink / yellow paper
                bool stamp = (x - 600) * (x - 600) + (y - 900) * (y - 900) < 60 * 60 && (x + y) % 3 == 0;
                if (stamp) (r, g, b) = (200, 40, 40);
                int o = (y * 800 + x) * 3;
                page.Data[o] = (byte)(r * light); page.Data[o + 1] = (byte)(g * light); page.Data[o + 2] = (byte)(b * light);
            }

        RgbImage clean = BackgroundFlattener.Flatten(page); // per-channel flattening (not used by color mode, kept for the tint case)

        // Text-free paper on the bright and the shadowed side: white in every channel (tint and shadow gone).
        foreach (int x0 in new[] { 10, 750 })
            for (int c = 0; c < 3; c++)
            {
                double m = 0;
                for (int y = 10; y < 50; y++) for (int x = x0; x < x0 + 40; x++) m += clean.Data[(y * 800 + x) * 3 + c];
                Assert.True(m / 1600 > 240, $"channel {c} at x={x0}: {m / 1600:0}");
            }
        // The red stamp stays clearly red.
        int o2 = (900 * 800 + 600) * 3;
        for (int dx = 0; dx < 3; dx++, o2 += 3)
            if ((600 + dx + 900) % 3 == 0)
                Assert.True(clean.Data[o2] > clean.Data[o2 + 1] + 80, $"stamp pixel {clean.Data[o2]},{clean.Data[o2 + 1]},{clean.Data[o2 + 2]}");
    }

    [Fact]
    public void Darkness_moves_Sauvola_k_around_the_tuned_default()
    {
        Assert.Equal(Binarizer.DefaultSauvolaK, DocumentFilter.SauvolaKFor(FilterOptions.DefaultDarkness), 3);
        Assert.True(DocumentFilter.SauvolaKFor(100) < DocumentFilter.SauvolaKFor(0));
        Assert.True(DocumentFilter.SauvolaKFor(100) > 0);
    }

    [Fact]
    public void Color_mode_without_cleaning_returns_the_page_untouched()
    {
        var rgb = new RgbImage(4, 3);
        rgb.Data[5] = 77;
        FilteredPage f = DocumentFilter.Apply(rgb, new FilterOptions(PageColorMode.Color, CleanBackground: false), 300);
        Assert.Same(rgb, f.Color);
        Assert.Null(f.Gray);
    }

    [Fact]
    public void Bilevel_png_round_trips_and_has_valid_chunks()
    {
        var img = new GrayImage(37, 5);
        var rnd = new Random(3);
        for (int i = 0; i < img.Data.Length; i++) img.Data[i] = rnd.Next(2) == 0 ? (byte)0 : (byte)255;

        byte[] png = PngWriter.EncodeBilevel(img);
        PngReader.PngData d = PngReader.Read(png);
        Assert.Equal((37, 5, 1, 0), (d.Width, d.Height, d.BitDepth, d.ColorType));
        AssertCrcsValid(png);

        byte[] raw = Inflate(d.ZlibData);
        int rowBytes = (37 + 7) / 8;
        Assert.Equal((rowBytes + 1) * 5, raw.Length);
        for (int y = 0; y < 5; y++)
        {
            Assert.Equal(0, raw[y * (rowBytes + 1)]); // filter None
            for (int x = 0; x < 37; x++)
            {
                bool white = (raw[y * (rowBytes + 1) + 1 + x / 8] & (0x80 >> (x % 8))) != 0;
                Assert.Equal(img[x, y] == 255, white);
            }
        }
    }

    [Fact]
    public void Gray8_png_round_trips()
    {
        var img = new GrayImage(5, 3, Enumerable.Range(0, 15).Select(i => (byte)(i * 17)).ToArray());
        PngReader.PngData d = PngReader.Read(PngWriter.EncodeGray8(img));
        Assert.Equal(8, d.BitDepth);
        byte[] raw = Inflate(d.ZlibData);
        for (int y = 0; y < 3; y++)
            Assert.Equal(img.Data.AsSpan(y * 5, 5).ToArray(), raw.AsSpan(y * 6 + 1, 5).ToArray());
    }

    [Fact]
    public void Crc32_matches_the_standard_check_value()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    private static void AssertCrcsValid(byte[] png)
    {
        int pos = 8;
        while (pos < png.Length)
        {
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            uint crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len));
            Assert.Equal(Crc32.Compute(png.AsSpan(pos + 4, 4 + len)), crc);
            pos += 12 + len;
        }
    }

    private static byte[] Inflate(byte[] zlib)
    {
        using var z = new ZLibStream(new MemoryStream(zlib), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    private static double MeanOf(GrayImage img, int x0, int y0, int w, int h)
    {
        double s = 0;
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++) s += img[x, y];
        return s / (w * h);
    }

    private static double MeanWhere(GrayImage img, GrayImage truth, int xFrom, int xTo)
    {
        double s = 0;
        int n = 0;
        for (int y = 0; y < img.Height; y++)
            for (int x = xFrom; x < xTo; x++)
                if (truth[x, y] < 128) { s += img[x, y]; n++; }
        return s / Math.Max(1, n);
    }
}

/// <summary>Measures process-wide allocations, so it must not run alongside other tests (xUnit runs
/// test classes in parallel; their allocations would be counted too).</summary>
[CollectionDefinition(nameof(FilterMemoryTests), DisableParallelization = true)]
public class FilterMemoryCollection;

[Collection(nameof(FilterMemoryTests))]
public class FilterMemoryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Black_and_white_on_a_full_A4_page_stays_within_a_phone_memory_budget()
    {
        // A4 at 300 DPI, the largest page the app renders.
        var page = new RgbImage(2480, 3508);
        var rnd = new Random(1);
        for (int i = 0; i < page.Data.Length; i += 3)
        {
            byte v = (byte)(rnd.Next(12) == 0 ? 40 : 230);
            page.Data[i] = page.Data[i + 1] = page.Data[i + 2] = v;
        }

        DocumentFilter.Apply(page, new FilterOptions(PageColorMode.BlackWhite), 300); // warm up (JIT, thread pool)
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        FilteredPage bw = DocumentFilter.Apply(page, new FilterOptions(PageColorMode.BlackWhite), 300);
        sw.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        byte[] png = PngWriter.EncodeBilevel(bw.Gray!);

        output.WriteLine($"allocated {allocated / 1048576.0:0.0} MB, {sw.ElapsedMilliseconds} ms, PNG {png.Length / 1024} KB");
        // Old integral-image Sauvola alone needed ~140 MB. Gray + flattened + binary + despeckle marks ~ 4 x 8.7 MB.
        Assert.True(allocated < 70L * 1024 * 1024, $"allocated {allocated / 1048576.0:0.0} MB");
    }
}
