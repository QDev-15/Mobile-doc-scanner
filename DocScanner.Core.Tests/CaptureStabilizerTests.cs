using DocScanner.Core.Camera;
using ImageCoreService;

namespace DocScanner.Core.Tests;

public class CaptureStabilizerTests
{
    private static readonly Quad Sheet = new(new(0.2, 0.15), new(0.8, 0.17), new(0.82, 0.85), new(0.18, 0.83));

    private static Quad Shift(Quad q, double dx) =>
        new(new(q.TopLeft.X + dx, q.TopLeft.Y), new(q.TopRight.X + dx, q.TopRight.Y),
            new(q.BottomRight.X + dx, q.BottomRight.Y), new(q.BottomLeft.X + dx, q.BottomLeft.Y));

    /// <summary>Feeds frames at 8 per second until a capture fires; returns the time it fired, or null.</summary>
    private static double? RunUntilCapture(CaptureStabilizer s, double from, double to, Func<double, Quad?> outline, double[]? signature = null)
    {
        for (double t = from; t <= to; t += 0.125)
            if (s.Update(t, outline(t), 0.8, signature).Cue == CaptureCue.Capture) return t;
        return null;
    }

    [Fact]
    public void A_sheet_held_still_is_captured_after_the_hold_time()
    {
        var s = new CaptureStabilizer();
        double? fired = RunUntilCapture(s, 0, 3, _ => Sheet);
        Assert.NotNull(fired);
        Assert.InRange(fired!.Value, 0.9, 1.2);
    }

    [Fact]
    public void Progress_rises_while_holding()
    {
        var s = new CaptureStabilizer();
        Assert.Equal(CaptureCue.Holding, s.Update(0, Sheet, 0.8).Cue);
        StabilizerState half = s.Update(0.45, Sheet, 0.8);
        Assert.Equal(CaptureCue.Holding, half.Cue);
        Assert.InRange(half.Progress, 0.45, 0.55);
    }

    [Fact]
    public void A_shaking_camera_never_captures()
    {
        var s = new CaptureStabilizer();
        Assert.Null(RunUntilCapture(s, 0, 5, t => Shift(Sheet, (int)(t * 8) % 2 == 0 ? 0 : 0.04)));
    }

    [Fact]
    public void Small_jitter_within_tolerance_still_captures()
    {
        var s = new CaptureStabilizer();
        Assert.NotNull(RunUntilCapture(s, 0, 3, t => Shift(Sheet, (int)(t * 8) % 2 == 0 ? 0 : 0.01)));
    }

    [Fact]
    public void Nothing_in_view_or_a_tiny_sheet_or_a_weak_detection_is_not_captured()
    {
        var s = new CaptureStabilizer();
        Assert.Null(RunUntilCapture(s, 0, 3, _ => null));
        var tiny = new Quad(new(0.45, 0.45), new(0.55, 0.45), new(0.55, 0.55), new(0.45, 0.55));
        Assert.Equal(CaptureCue.TooFar, s.Update(4, tiny, 0.9).Cue);
        Assert.Null(RunUntilCapture(s, 4, 7, _ => tiny));
        for (double t = 8; t < 11; t += 0.125) Assert.NotEqual(CaptureCue.Capture, s.Update(t, Sheet, 0.45).Cue);
    }

    [Fact]
    public void A_sheet_cut_by_the_frame_is_not_captured()
    {
        var s = new CaptureStabilizer();
        var cut = new Quad(new(0.0, 0.1), new(0.8, 0.12), new(0.82, 0.9), new(0.05, 0.88));
        Assert.Equal(CaptureCue.OutOfFrame, s.Update(0, cut, 0.9).Cue);
        Assert.Null(RunUntilCapture(s, 0, 3, _ => cut));
    }

    [Fact]
    public void The_same_page_is_not_captured_twice()
    {
        var s = new CaptureStabilizer();
        double[] page1 = Enumerable.Repeat(200.0, 64).ToArray();
        double fired = RunUntilCapture(s, 0, 3, _ => Sheet, page1)!.Value;
        s.Captured(Sheet, page1);
        Assert.Null(RunUntilCapture(s, fired, fired + 5, _ => Sheet, page1));
        Assert.Equal(CaptureCue.Captured, s.Update(fired + 5.1, Sheet, 0.8, page1).Cue);
    }

    [Fact]
    public void The_next_page_is_captured_after_the_sheet_is_lifted()
    {
        var s = new CaptureStabilizer();
        s.Captured(Sheet, null);
        Assert.Null(RunUntilCapture(s, 0, 2, _ => Sheet));
        // Page turned: nothing detected for a moment, then the next sheet in the same place.
        Assert.NotNull(RunUntilCapture(s, 2, 5, t => t < 2.7 ? null : Sheet));
    }

    [Fact]
    public void A_brief_detection_dropout_does_not_count_as_a_new_page()
    {
        var s = new CaptureStabilizer();
        s.Captured(Sheet, null);
        Assert.Null(RunUntilCapture(s, 0, 4, t => Math.Abs(t - 2) < 0.2 ? null : Sheet));
    }

    [Fact]
    public void The_next_page_laid_in_the_same_place_is_recognised_by_its_content()
    {
        var s = new CaptureStabilizer();
        double[] page1 = Enumerable.Repeat(200.0, 64).ToArray();
        double[] page2 = Enumerable.Range(0, 64).Select(i => i % 3 == 0 ? 120.0 : 205.0).ToArray();
        s.Captured(Sheet, page1);
        Assert.NotNull(RunUntilCapture(s, 0, 3, _ => Sheet, page2));
    }

    [Fact]
    public void Moving_to_another_sheet_releases_the_lock()
    {
        var s = new CaptureStabilizer();
        s.Captured(Sheet, null);
        Quad other = Shift(Sheet, -0.15);
        Assert.NotNull(RunUntilCapture(s, 0, 3, _ => other));
    }

    [Fact]
    public void Frame_signature_tells_pages_apart()
    {
        var blank = new RgbImage(64, 48);
        Array.Fill(blank.Data, (byte)220);
        var text = new RgbImage(64, 48);
        Array.Fill(text.Data, (byte)220);
        for (int y = 10; y < 40; y += 4)
            for (int x = 0; x < 64 * 3; x++) text.Data[y * 64 * 3 + x] = 20;
        double same = CaptureStabilizer.SignatureDistance(CaptureStabilizer.FrameSignature(blank), CaptureStabilizer.FrameSignature(blank));
        double different = CaptureStabilizer.SignatureDistance(CaptureStabilizer.FrameSignature(blank), CaptureStabilizer.FrameSignature(text));
        Assert.Equal(0, same);
        Assert.True(different > new CaptureStabilizer().ReleaseContentChange, $"distance {different}");
    }
}
