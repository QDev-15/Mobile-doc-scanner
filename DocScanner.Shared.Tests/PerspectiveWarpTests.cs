using ImageCoreService;
using Xunit.Abstractions;

namespace ImageCore.Shared.Tests;

public class PerspectiveWarpTests(ITestOutputHelper output)
{
    private static Quad Q(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3) =>
        new(new PointD(x0, y0), new PointD(x1, y1), new PointD(x2, y2), new PointD(x3, y3));

    private static readonly SceneBuilder.Options PlainDesk = new((40, 40, 45), (235, 232, 222));

    /// <summary>The paper texture itself (500x700), rendered with the identity mapping.</summary>
    private static RgbImage Texture() =>
        SceneBuilder.Render(500, 700, Q(0, 0, 500, 0, 500, 700, 0, 700), PlainDesk);

    private static double MeanAbsError(RgbImage a, RgbImage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        long sum = 0;
        for (int i = 0; i < a.Data.Length; i++) sum += Math.Abs(a.Data[i] - b.Data[i]);
        return (double)sum / a.Data.Length;
    }

    [Fact]
    public void Warping_the_whole_image_onto_itself_changes_nothing()
    {
        RgbImage tex = Texture();
        RgbImage same = PerspectiveWarp.Warp(tex, Q(0, 0, 500, 0, 500, 700, 0, 700), 500, 700);
        int worst = 0;
        for (int i = 0; i < tex.Data.Length; i++) worst = Math.Max(worst, Math.Abs(tex.Data[i] - same.Data[i]));
        Assert.True(worst <= 1, $"largest difference {worst}");
    }

    [Theory]
    [InlineData("tilted", 100, 80, 700, 120, 640, 900, 60, 860)]
    [InlineData("strong perspective", 260, 60, 560, 60, 760, 940, 40, 940)]
    [InlineData("rotated 30 degrees", 330, 40, 780, 300, 470, 850, 20, 590)]
    public void A_photographed_page_is_straightened_back_to_its_texture(string name, double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var quad = Q(x0, y0, x1, y1, x2, y2, x3, y3);
        RgbImage photo = SceneBuilder.Render(800, 1000, quad, PlainDesk);

        RgbImage flat = PerspectiveWarp.Warp(photo, quad, 500, 700);

        double mae = MeanAbsError(Texture(), flat);
        output.WriteLine($"{name}: mean abs error {mae:0.00} grey levels");
        // Thin text strokes cannot line up to the pixel after two resamplings; a wrong transform gives 60+.
        Assert.True(mae < 9, $"{name}: mean abs error {mae:0.00}");
    }

    [Fact]
    public void Shrinking_a_dense_photo_to_a_page_keeps_it_faithful_thanks_to_supersampling()
    {
        var quad = Q(120, 90, 1100, 150, 1000, 1400, 60, 1330); // ~1.9x denser than the 500x700 output
        RgbImage photo = SceneBuilder.Render(1200, 1500, quad, PlainDesk);

        RgbImage flat = PerspectiveWarp.Warp(photo, quad, 500, 700);

        double mae = MeanAbsError(Texture(), flat);
        output.WriteLine($"dense photo: mean abs error {mae:0.00}");
        Assert.True(mae < 9, $"mean abs error {mae:0.00}");
    }

    [Fact]
    public void Corners_of_the_output_come_from_the_corners_of_the_quad()
    {
        // A photo that is a flat colour gradient per corner: each output corner must show its own quad corner's colour.
        var photo = new RgbImage(400, 300);
        for (int y = 0; y < 300; y++)
            for (int x = 0; x < 400; x++)
            {
                int o = (y * 400 + x) * 3;
                photo.Data[o] = (byte)(x * 255 / 399);      // red grows to the right
                photo.Data[o + 1] = (byte)(y * 255 / 299);  // green grows downwards
            }
        var quad = Q(50, 40, 350, 60, 330, 260, 30, 240);

        RgbImage flat = PerspectiveWarp.Warp(photo, quad, 200, 300);

        // top-left of the output ~ quad TL, bottom-right ~ quad BR
        Assert.InRange(flat.Data[0], 50 * 255 / 399 - 4, 50 * 255 / 399 + 12);
        int br = ((300 - 1) * 200 + 199) * 3;
        Assert.InRange(flat.Data[br], 330 * 255 / 399 - 12, 330 * 255 / 399 + 4);
        Assert.InRange(flat.Data[br + 1], 260 * 255 / 299 - 12, 260 * 255 / 299 + 4);
    }

    [Fact]
    public void Parts_of_the_outline_outside_the_photo_come_out_white_and_the_rest_is_untouched()
    {
        // A sheet cut by the frame: its left 100 px are off-picture.
        var photo = new RgbImage(200, 200);
        Array.Fill(photo.Data, (byte)100);
        var quad = Q(-100, 50, 150, 50, 150, 150, -100, 150); // 250 x 100

        RgbImage flat = PerspectiveWarp.Warp(photo, quad, 250, 100);

        byte At(int x, int y) => flat.Data[(y * 250 + x) * 3];
        Assert.Equal(255, At(20, 50));   // x = -80 in the photo: not photographed
        Assert.Equal(255, At(95, 50));   // x = -5
        Assert.Equal(100, At(105, 50));  // x = 5: inside the photo
        Assert.Equal(100, At(240, 50));
    }

    [Fact]
    public void Output_size_follows_the_longer_sides_and_respects_the_caps()
    {
        // 1000 px wide at the top, 900 at the bottom; left side 1500.8 / right 1400 tall -> 1000 x 1501
        Assert.Equal((1000, 1501), PerspectiveWarp.OutputSize(Q(0, 0, 1000, 0, 950, 1400, 50, 1500), 4000, 100_000_000));

        // capped by the long edge, keeping the aspect ratio
        (int w, int h) = PerspectiveWarp.OutputSize(Q(0, 0, 6000, 0, 6000, 8000, 0, 8000), 3508, 100_000_000);
        Assert.Equal(3508, h);
        Assert.InRange(w, 2630, 2632);

        // capped by the pixel budget
        (int w2, int h2) = PerspectiveWarp.OutputSize(Q(0, 0, 5000, 0, 5000, 5000, 0, 5000), 100_000, 4_000_000);
        Assert.InRange((long)w2 * h2, 3_990_000, 4_010_000);

        // never larger than the source region
        Assert.Equal((400, 300), PerspectiveWarp.OutputSize(Q(0, 0, 400, 0, 400, 300, 0, 300), 3508, 8_700_000));
    }

    [Fact]
    public void A4_size_has_the_standard_ratio_and_follows_the_outlines_orientation()
    {
        // taller than wide -> portrait, never coarser than the outline
        Assert.Equal((1000, 1414), PerspectiveWarp.A4Size(Q(0, 0, 1000, 0, 1000, 1300, 0, 1300), 4000, 100_000_000));
        // wider than tall -> landscape
        Assert.Equal((1414, 1000), PerspectiveWarp.A4Size(Q(0, 0, 1300, 0, 1300, 1000, 0, 1000), 4000, 100_000_000));
        // a very tall outline: the long side follows the height
        Assert.Equal((707, 1000), PerspectiveWarp.A4Size(Q(0, 0, 500, 0, 500, 1000, 0, 1000), 4000, 100_000_000));
        // perspective: the longer of each pair of sides counts
        (int w, int h) = PerspectiveWarp.A4Size(Q(0, 0, 800, 0, 1000, 1500, -200, 1500), 4000, 100_000_000);
        Assert.Equal((1200, 1697), (w, h)); // bottom 1200 wide, right side 1500: portrait, long = max(1500, 1200 x 1.414)
    }

    [Fact]
    public void A4_size_is_capped_at_300_dpi()
    {
        (int w, int h) = PerspectiveWarp.A4Size(Q(0, 0, 6000, 0, 6000, 8000, 0, 8000), 3508, 8_700_000);
        Assert.InRange(h, 3506, 3508);
        Assert.InRange(w, 2479, 2482);
        Assert.Equal(PerspectiveWarp.A4Ratio, (double)h / w, 2);
    }

    [Fact]
    public void Resize_hits_the_requested_size_and_keeps_a_flat_colour()
    {
        var img = new RgbImage(1000, 700);
        Array.Fill(img.Data, (byte)123);

        RgbImage small = img.Resize(333, 211);

        Assert.Equal((333, 211), (small.Width, small.Height));
        Assert.All(small.Data, v => Assert.Equal(123, v));
    }

    [Fact]
    public void GrayImage_Resize_hits_the_requested_size_and_keeps_a_flat_level()
    {
        var img = new GrayImage(1000, 700);
        Array.Fill(img.Data, (byte)77);

        GrayImage small = img.Resize(333, 211);

        Assert.Equal((333, 211), (small.Width, small.Height));
        Assert.All(small.Data, v => Assert.Equal(77, v));
    }

    /// <summary>What black-and-white PDF export relies on (2026-09-28c: pages stay anti-aliased instead of 1-bit at
    /// every quality): a hard black/white edge, shrunk, comes out with a genuine gray step at the edge -- true
    /// anti-aliasing from the pixels it averaged, not the original hard 0/255.</summary>
    [Fact]
    public void GrayImage_Resize_turns_a_hard_edge_into_a_soft_one()
    {
        var img = new GrayImage(65, 40);
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 65; x++)
                img.Data[y * 65 + x] = x < 33 ? (byte)0 : (byte)255;

        GrayImage small = img.Resize(20, 12);

        Assert.Equal(0, small[0, 6]);
        Assert.Equal(255, small[19, 6]);
        Assert.Contains(Enumerable.Range(0, 20), x => small[x, 6] is > 0 and < 255); // a genuine soft step at the old edge
    }
}

public class A4ShapeTests
{
    private static Quad R(double w, double h) => new(new PointD(0, 0), new PointD(w, 0), new PointD(w, h), new PointD(0, h));

    [Theory]
    [InlineData(1000, 1414, true)]   // exact A4
    [InlineData(1000, 1300, true)]   // A4 seen with some perspective / loose corners
    [InlineData(1414, 1000, true)]   // landscape
    [InlineData(1000, 1080, false)]  // nearly square: a sheet cut off by the frame (owner's page 7)
    [InlineData(500, 1000, false)]   // long receipt
    [InlineData(1000, 520, false)]   // wide strip / banner (1.9 : 1)
    public void Only_sheet_shaped_outlines_count_as_A4(double w, double h, bool a4) =>
        Assert.Equal(a4, PerspectiveWarp.IsA4Like(R(w, h)));
}
