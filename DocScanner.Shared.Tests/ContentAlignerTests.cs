using ImageCoreService;

namespace ImageCore.Shared.Tests;

public class ContentAlignerTests
{
    /// <summary>A text page whose line at height v leans <paramref name="topDeg"/> at the top and <paramref name="bottomDeg"/>
    /// at the bottom (degrees, positive = clockwise): each row of the level page is drawn along its lean.</summary>
    private static RgbImage Leaning(double topDeg, double bottomDeg, int w = 900, int h = 1200)
    {
        GrayImage level = SyntheticPages.TextPage(w, h);
        var page = new RgbImage(w, h);
        Array.Fill(page.Data, (byte)255);
        double cx = w / 2.0;
        // Each text line shifted sideways by its own amount: word gaps of real text do not line up down the page (the
        // synthetic page's do, which would give its columns as sharp a structure as its lines).
        var rng = new Random(3);
        int[] shift = Enumerable.Range(0, h / 32 + 2).Select(_ => rng.Next(70)).ToArray();
        for (int y = 0; y < h; y++)
        {
            double t = Math.Tan((topDeg + (bottomDeg - topDeg) * y / h) * Math.PI / 180);
            int dxLine = shift[Math.Max(0, y - 90 + 16) / 32];
            for (int x = 0; x < w; x++)
            {
                int lx = x + dxLine;
                if (lx < 80 || lx >= w - 80 || level.Data[y * w + lx] >= 128) continue;
                int ty = (int)Math.Round(y + t * (x - cx));
                if ((uint)ty >= (uint)h) continue;
                int o = (ty * w + x) * 3;
                page.Data[o] = page.Data[o + 1] = page.Data[o + 2] = 30;
            }
        }
        return page;
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(-1.2)]
    [InlineData(0.6)]
    public void A_page_leaning_by_a_small_angle_is_measured_and_levelled(double deg)
    {
        RgbImage page = Leaning(deg, deg);
        LineTilt tilt = ContentAligner.Measure(page)!.Value;
        Assert.InRange(tilt.Angle, deg - 0.2, deg + 0.2);
        Assert.InRange(tilt.Slope, -0.4, 0.4);

        RgbImage levelled = ContentAligner.Align(page, tilt);
        LineTilt? after = ContentAligner.Measure(levelled);
        Assert.True(after == null || Math.Abs(after.Value.Angle) < 0.2, $"still leaning {after?.Angle:0.00}");
    }

    [Theory]
    [InlineData(2.0, 1)]
    [InlineData(-1.5, 1)]
    [InlineData(1.2, 3)]
    public void A_sideways_page_with_its_text_running_down_is_measured_along_the_columns(double deg, int quarterTurns)
    {
        // Turning the page does not change how its content is turned relative to the page: the same lean comes out.
        RgbImage page = Leaning(deg, deg).RotateClockwise(quarterTurns);
        LineTilt tilt = ContentAligner.Measure(page)!.Value;
        Assert.InRange(tilt.Angle, deg - 0.25, deg + 0.25);

        RgbImage levelled = ContentAligner.Align(page, tilt).RotateClockwise(4 - quarterTurns);
        LineTilt? after = ContentAligner.Measure(levelled);
        Assert.True(after == null || Math.Abs(after.Value.Angle) < 0.25, $"still leaning {after?.Angle:0.00}");
    }

    [Fact]
    public void A_lean_that_changes_from_top_to_bottom_is_followed()
    {
        RgbImage page = Leaning(1.5, -1.0);
        LineTilt tilt = ContentAligner.Measure(page)!.Value;
        Assert.InRange(tilt.At(0.1), 0.9, 1.7);
        Assert.InRange(tilt.At(0.9), -1.2, -0.4);

        LineTilt? after = ContentAligner.Measure(ContentAligner.Align(page, tilt));
        Assert.True(after == null || (Math.Abs(after.Value.At(0.1)) < 0.35 && Math.Abs(after.Value.At(0.9)) < 0.35),
            $"after: {after?.At(0.1):0.00} .. {after?.At(0.9):0.00}");
    }

    [Fact]
    public void A_level_page_is_left_alone()
    {
        RgbImage page = Leaning(0, 0);
        Assert.Null(ContentAligner.Measure(page));
        Assert.Same(page, ContentAligner.Align(page, null));
    }

    [Fact]
    public void Blank_paper_and_noise_have_no_lines_to_level()
    {
        var blank = new RgbImage(800, 1000);
        Array.Fill(blank.Data, (byte)240);
        Assert.Null(ContentAligner.Measure(blank));

        var noise = new RgbImage(800, 1000);
        new Random(5).NextBytes(noise.Data);
        Assert.Null(ContentAligner.Measure(noise));
    }

    [Fact]
    public void A_page_turned_well_beyond_a_residual_error_is_left_as_it_is()
    {
        RgbImage page = Leaning(12, 12);
        LineTilt? tilt = ContentAligner.Measure(page);
        Assert.True(tilt == null || Math.Abs(tilt.Value.Angle) <= ContentAligner.MaxAngle);
    }

    /// <summary>Regression for owner's report 2026-09-29 ("T1" batch): a page's own straight bottom edge came out
    /// rippled after aligning, because an earlier version of <see cref="LineTilt"/> followed the four measured bands
    /// exactly (piecewise linear between their centres) -- two adjacent bands disagreeing by nothing more than
    /// measurement noise was enough to put a visible wave in a perfectly straight line. <see cref="LineTilt.At"/> is now
    /// only ever <c>Angle + Slope * (v - 0.5)</c> (affine), which cannot bend a straight line into more than one curve no
    /// matter what Angle/Slope it is given -- checked here directly at the geometry level, without needing measurement
    /// noise to reproduce it.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 0)]
    [InlineData(-6, 0)]
    [InlineData(2, 3)]
    [InlineData(-3, -3)]
    public void A_straight_edge_never_comes_out_wavy(double angle, double slope)
    {
        const int w = 900, h = 1200, edgeY = 1150; // a solid band across the bottom, like the frame outside the page
        var page = new RgbImage(w, h);
        Array.Fill(page.Data, (byte)255);
        for (int y = edgeY; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                page.Data[o] = page.Data[o + 1] = page.Data[o + 2] = 0;
            }

        RgbImage aligned = ContentAligner.Align(page, new LineTilt(angle, slope, 4));

        // For every column, the topmost dark row (where the boundary crosses it) must move in only one direction as x
        // increases -- a real (linear) tilt or slope draws one straight or gently sloped line, never a wave.
        int[] boundary = new int[w];
        for (int x = 0; x < w; x++)
        {
            int y = 0;
            while (y < h && aligned.Data[(y * w + x) * 3] > 128) y++;
            boundary[x] = y;
        }
        int direction = 0;
        for (int x = 1; x < w; x++)
        {
            int d = Math.Sign(boundary[x] - boundary[x - 1]);
            if (d == 0) continue;
            if (direction != 0 && d != direction)
                Assert.Fail($"boundary changed direction at x={x} (angle {angle}, slope {slope}): {string.Join(',', boundary[Math.Max(0, x - 3)..Math.Min(w, x + 3)])}");
            direction = d;
        }
    }
}
