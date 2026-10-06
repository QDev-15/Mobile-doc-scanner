using ImageCoreService;

namespace ImageCore.Shared.Tests;

public class ImagingTests
{
    private static int InkPixels(GrayImage img) => img.Data.Count(v => v == 0);

    [Fact]
    public void Sauvola_keeps_dim_paper_white_where_a_fixed_threshold_does_not()
    {
        GrayImage page = SyntheticPages.TextPage(paper: 110); // dim scan: paper below the old 128 cut
        int sauvola = InkPixels(Binarizer.Binarize(page, BinarizationMethod.Sauvola, SyntheticPages.Dpi));
        int fixed128 = InkPixels(Binarizer.Threshold(page, 128));
        Assert.True(fixed128 > page.Data.Length / 2, "fixed threshold turns dim paper black");
        Assert.True(sauvola < page.Data.Length / 10, $"sauvola ink px = {sauvola}");
        Assert.True(sauvola > 1000, "text strokes must survive");
    }

    [Fact]
    public void Otsu_splits_a_bimodal_histogram_between_the_modes()
    {
        var hist = new long[256];
        hist[40] = 1000;
        hist[200] = 9000;
        int t = Binarizer.OtsuThreshold(hist);
        Assert.InRange(t, 40, 199);
    }

    [Fact]
    public void Threshold_marks_ink_as_zero_and_paper_as_255()
    {
        var img = new GrayImage(2, 1, [10, 200]);
        GrayImage bin = Binarizer.Threshold(img, 128);
        Assert.Equal(0, bin[0, 0]);
        Assert.Equal(255, bin[1, 0]);
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(-4.0)]
    public void DetectSkew_finds_the_angle(double skew)
    {
        GrayImage page = SyntheticPages.TextPage(skewDeg: skew);
        double a = DocumentCleanup.DetectSkew(page, SyntheticPages.Dpi);
        Assert.InRange(a, skew - 0.4, skew + 0.4);
    }

    [Fact]
    public void DetectSkew_on_a_straight_page_is_zero()
    {
        double a = DocumentCleanup.DetectSkew(SyntheticPages.TextPage(), SyntheticPages.Dpi);
        Assert.InRange(a, -0.2, 0.2);
    }

    [Fact]
    public void DetectContentBounds_finds_the_sheet_inside_a_black_border()
    {
        GrayImage page = SyntheticPages.TextPage();
        GrayImage framed = SyntheticPages.WithBlackBorder(page, 60);
        var r = DocumentCleanup.DetectContentBounds(framed, SyntheticPages.Dpi);
        Assert.InRange(r.X, 55, 70);
        Assert.InRange(r.Y, 55, 70);
        Assert.InRange(r.Width, page.Width - 15, page.Width + 5);
        Assert.InRange(r.Height, page.Height - 15, page.Height + 5);
    }

    [Fact]
    public void DetectContentBounds_without_border_is_the_full_image()
    {
        GrayImage page = SyntheticPages.TextPage();
        var r = DocumentCleanup.DetectContentBounds(page, SyntheticPages.Dpi);
        Assert.Equal(new System.Drawing.Rectangle(0, 0, page.Width, page.Height), r);
    }

    [Fact]
    public void Despeckle_removes_dust_but_keeps_real_strokes()
    {
        var bin = new GrayImage(100, 100);
        Array.Fill(bin.Data, (byte)255);
        bin[10, 10] = 0;                       // 1 px dust
        bin[50, 50] = 0; bin[51, 50] = 0;      // 2 px dust
        for (int y = 70; y < 80; y++)          // 10x10 blob = real content
            for (int x = 70; x < 80; x++) bin[x, y] = 0;

        int removed = DocumentCleanup.Despeckle(bin, maxArea: 4);

        Assert.Equal(2, removed);
        Assert.Equal(255, bin[10, 10]);
        Assert.Equal(255, bin[50, 50]);
        Assert.Equal(100, InkPixels(bin));
    }

    [Fact]
    public void Downscale_averages_and_crop_copies()
    {
        var img = new GrayImage(4, 4);
        for (int i = 0; i < 16; i++) img.Data[i] = (byte)(i < 8 ? 0 : 200);
        GrayImage half = img.Downscale(2);
        Assert.Equal((2, 2), (half.Width, half.Height));
        Assert.Equal(0, half[0, 0]);
        Assert.Equal(200, half[0, 1]);

        GrayImage part = img.Crop(new System.Drawing.Rectangle(1, 2, 2, 2));
        Assert.Equal((2, 2), (part.Width, part.Height));
        Assert.All(part.Data, v => Assert.Equal(200, v));
    }
}
