using DocScanner.Core;
using ImageCoreService;

namespace DocScanner.Core.Tests;

/// <summary>The result screen's staged preview: same looks as the saved page, darkness / brightness as a cheap
/// re-threshold, quarter turns without refiltering.</summary>
public class LookPreviewTests
{
    /// <summary>A photographed page: shaded paper, rows of strokes of varying darkness (some faint).</summary>
    private static RgbImage Page(int w = 600, int h = 800)
    {
        var rnd = new Random(7);
        var img = new RgbImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int paper = 225 - 40 * x / w - 20 * y / h;
                int v = paper;
                if ((y / 8) % 3 == 0 && (x / 11) % 5 != 4) v = (y / 24) % 4 == 0 ? 150 : 50; // every 4th row faint (pencil)
                v = Math.Clamp(v + rnd.Next(-6, 7), 0, 255);
                int o = (y * w + x) * 3;
                img.Data[o] = (byte)Math.Min(255, v + 6);
                img.Data[o + 1] = (byte)v;
                img.Data[o + 2] = (byte)Math.Max(0, v - 10);
            }
        return img;
    }

    private static int Ink(PreviewFrame f) => f.Gray!.Data.Count(v => v == 0);

    [Fact]
    public void Color_is_the_page_itself_and_gray_is_computed_once()
    {
        RgbImage page = Page();
        var preview = new LookPreview(page);
        Assert.Same(page, preview.Render(new FilterOptions(PageColorMode.Color)).Color);
        GrayImage a = preview.Render(new FilterOptions(PageColorMode.Gray)).Gray!;
        Assert.Same(a, preview.Render(new FilterOptions(PageColorMode.Gray)).Gray); // cached stage
    }

    [Fact]
    public void Black_and_white_matches_the_saved_page_filter_at_the_same_size()
    {
        RgbImage page = Page();
        var preview = new LookPreview(page);
        foreach (bool clean in new[] { true, false })
        {
            var look = new FilterOptions(PageColorMode.BlackWhite, 60, clean) { Tone = new ToneAdjust(15, 0) };
            byte[] live = preview.Render(look).Gray!.Data;
            byte[] saved = DocumentFilter.Apply(page, look, preview.Dpi).Gray!.Data;
            int differ = live.Zip(saved).Count(p => p.First != p.Second);
            Assert.True(differ <= live.Length / 2000, $"clean={clean}: {differ} pixels differ"); // float statistics: rare ties
        }
    }

    [Fact]
    public void Darkness_and_brightness_change_the_ink()
    {
        var preview = new LookPreview(Page());
        int Look(int darkness, int brightness) =>
            Ink(preview.Render(new FilterOptions(PageColorMode.BlackWhite, darkness) { Tone = new ToneAdjust(brightness, 0) }));
        Assert.True(Look(90, 0) > Look(50, 0));
        Assert.True(Look(10, 0) < Look(50, 0));
        Assert.True(Look(50, -40) > Look(50, 0));
        Assert.True(Look(50, 40) < Look(50, 0));
    }

    [Fact]
    public void A_turned_preview_gives_the_turned_picture_without_filtering_again()
    {
        var preview = new LookPreview(Page(300, 420));
        var look = new FilterOptions(PageColorMode.BlackWhite, 55, CleanBackground: false);
        GrayImage before = preview.Render(look).Gray!;
        for (int turns = 1; turns <= 3; turns++)
        {
            LookPreview turned = preview.RotateClockwise(turns);
            Assert.Equal(preview.Page.RotateClockwise(turns).Data, turned.Page.Data);
            Assert.Equal(before.RotateClockwise(turns).Data, turned.Render(look).Gray!.Data);
        }
    }

    [Fact]
    public void Warming_does_not_change_what_is_shown()
    {
        RgbImage page = Page(300, 420);
        var cold = new LookPreview(page);
        var warm = new LookPreview(page);
        warm.Warm(cleanBackground: true);
        warm.Warm(cleanBackground: false);
        var look = new FilterOptions(PageColorMode.BlackWhite, 40);
        Assert.Equal(cold.Render(look).Gray!.Data, warm.Render(look).Gray!.Data);
    }
}
