using ImageCoreService;
using Xunit.Abstractions;

namespace ImageCore.Shared.Tests;

public class EdgeDetectorTests(ITestOutputHelper output)
{
    private static Quad Q(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3) =>
        new(new PointD(x0, y0), new PointD(x1, y1), new PointD(x2, y2), new PointD(x3, y3));

    private static Quad Rotate(Quad q, double deg, double cx, double cy)
    {
        double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        PointD R(PointD p) => new(cx + (p.X - cx) * c - (p.Y - cy) * s, cy + (p.X - cx) * s + (p.Y - cy) * c);
        return new Quad(R(q.TopLeft), R(q.TopRight), R(q.BottomRight), R(q.BottomLeft));
    }

    private static readonly (byte, byte, byte) DarkDesk = (60, 45, 35);
    private static readonly (byte, byte, byte) Sheet = (235, 232, 222);

    public static IEnumerable<object[]> Scenarios()
    {
        Quad straight = Q(150, 60, 650, 70, 640, 540, 160, 530);
        yield return ["dark desk, slightly tilted", 800, 600, straight, new SceneBuilder.Options(DarkDesk, Sheet)];
        yield return ["perspective (top narrower)", 800, 600, Q(220, 50, 600, 80, 700, 560, 100, 540),
            new SceneBuilder.Options((110, 110, 115), Sheet)];
        yield return ["rotated 25 degrees", 800, 600, Rotate(Q(200, 100, 600, 100, 600, 500, 200, 500), 25, 400, 300),
            new SceneBuilder.Options((90, 90, 90), Sheet)];
        yield return ["rotated 60 degrees", 800, 600, Rotate(Q(220, 130, 580, 130, 580, 470, 220, 470), 60, 400, 300),
            new SceneBuilder.Options((70, 80, 90), Sheet)];
        yield return ["light desk, low contrast", 800, 600, straight,
            new SceneBuilder.Options((200, 196, 186), (240, 238, 232))];
        yield return ["wood grain + shadow + noise", 800, 600, Q(140, 70, 660, 50, 650, 550, 150, 540),
            new SceneBuilder.Options((150, 105, 70), Sheet, Stripes: true, Shadow: 0.3, Noise: 6)];
        yield return ["color-only contrast (green desk)", 800, 600, straight,
            new SceneBuilder.Options((105, 160, 100), (150, 150, 150))];
        yield return ["clutter on the table", 800, 600, Q(200, 70, 700, 90, 690, 540, 190, 520),
            new SceneBuilder.Options((70, 70, 75), Sheet, Clutter: true, Noise: 4)];
        yield return ["portrait phone frame 3:4", 600, 800, Q(90, 100, 520, 120, 500, 700, 80, 690),
            new SceneBuilder.Options((80, 60, 50), Sheet, Shadow: 0.2, Noise: 5)];
        yield return ["heavy noise", 800, 600, Q(150, 60, 650, 70, 640, 540, 160, 530),
            new SceneBuilder.Options((85, 80, 75), Sheet, Noise: 22, Seed: 5)];
        yield return ["strong perspective (35% narrower top)", 800, 600, Q(270, 60, 530, 60, 720, 550, 80, 550),
            new SceneBuilder.Options((70, 70, 70), Sheet, Noise: 4)];
        yield return ["small sheet in a big frame", 800, 600, Q(300, 200, 500, 205, 495, 400, 305, 395),
            new SceneBuilder.Options((120, 100, 80), Sheet, Stripes: true, Noise: 3)];
        yield return ["wood grain running along the edges", 800, 600, Q(150, 60, 650, 60, 650, 540, 150, 540),
            new SceneBuilder.Options((150, 110, 75), Sheet, Stripes: true, Shadow: 0.15, Noise: 8, Seed: 9)];
        yield return ["very low contrast (20 levels)", 800, 600, Q(150, 60, 650, 70, 640, 540, 160, 530),
            new SceneBuilder.Options((215, 212, 205), (236, 233, 226), Noise: 2)];
        yield return ["light desk, soft shadow, perspective", 800, 600, Q(250, 45, 590, 60, 700, 555, 105, 530),
            new SceneBuilder.Options((205, 198, 186), Sheet, Shadow: 0.2, DropShadow: 14, Noise: 4)];
        yield return ["dark desk, soft shadow, sheet cut off at the bottom", 800, 600, Q(150, 40, 690, 25, 700, 760, 130, 780),
            new SceneBuilder.Options((70, 80, 92), Sheet, DropShadow: 12, Noise: 3)];
        yield return ["sheet cut off by right and bottom", 800, 600, Q(160, 90, 900, 120, 880, 720, 140, 680),
            new SceneBuilder.Options(DarkDesk, Sheet)];
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Finds_the_sheet(string name, int w, int h, Quad truth, SceneBuilder.Options opts)
    {
        RgbImage scene = SceneBuilder.Render(w, h, truth, opts);
        QuadDetection d = new DocumentEdgeDetector { Trace = output.WriteLine }.Detect(scene);
        (double err, double iou) = SceneBuilder.Score(truth, d.Quad, w, h);
        string corners = string.Join(' ', d.Quad.Scale(w, h).ToArray().Select(p => $"({p.X:0},{p.Y:0})"));
        output.WriteLine($"{name}: detected={d.Detected} confidence={d.Confidence:0.00} IoU={iou:0.000} cornerErr={err:P1} corners {corners}");

        Assert.True(d.Detected, $"{name}: not detected (confidence {d.Confidence:0.00})");
        Assert.True(iou >= 0.92, $"{name}: IoU {iou:0.000}");
        Assert.True(err <= 0.03, $"{name}: corner error {err:P1} of the diagonal");
    }

    /// <summary>The live camera setting (<see cref="DocumentEdgeDetector.Live"/>) on every scene with the whole sheet in view:
    /// same accuracy, and confident enough for the camera's automatic capture.</summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Live_mode_finds_a_sheet_that_is_wholly_in_view(string name, int w, int h, Quad truth, SceneBuilder.Options opts)
    {
        if (truth.ToArray().Any(p => p.X < 0 || p.Y < 0 || p.X > w || p.Y > h)) return; // cut off: not a live-camera case
        RgbImage scene = SceneBuilder.Render(w, h, truth, opts);
        QuadDetection d = DocumentEdgeDetector.Live().Detect(LiveFrame(scene));
        (double err, double iou) = SceneBuilder.Score(truth, d.Quad, w, h);
        output.WriteLine($"{name}: detected={d.Detected} confidence={d.Confidence:0.00} IoU={iou:0.000} cornerErr={err:P1}");

        Assert.True(d.Detected, $"{name}: not detected (confidence {d.Confidence:0.00})");
        Assert.True(iou >= 0.92, $"{name}: IoU {iou:0.000}");
        Assert.True(d.Confidence >= LiveCameraThreshold, $"{name}: confidence {d.Confidence:0.00}");
    }

    /// <summary>A frame as the live camera hands it over: long edge <see cref="DocumentEdgeDetector.LiveAnalysisEdge"/>.</summary>
    private static RgbImage LiveFrame(RgbImage scene)
    {
        double s = (double)DocumentEdgeDetector.LiveAnalysisEdge / Math.Max(scene.Width, scene.Height);
        return scene.Resize((int)Math.Round(scene.Width * s), (int)Math.Round(scene.Height * s));
    }

    /// <summary>What the live camera requires for an automatic capture (CaptureStabilizer.MinConfidence).</summary>
    private const double LiveCameraThreshold = 0.5;

    [Fact]
    public void Live_mode_does_not_take_a_cluttered_table_for_a_sheet()
    {
        var far = new Quad(new PointD(-5000, -5000), new PointD(-4900, -5000), new PointD(-4900, -4900), new PointD(-5000, -4900));
        RgbImage scene = SceneBuilder.Render(800, 600, far, new SceneBuilder.Options((150, 105, 70), Sheet, Stripes: true, Clutter: true, Noise: 6, Seed: 4));
        QuadDetection d = DocumentEdgeDetector.Live().Detect(LiveFrame(scene));
        output.WriteLine($"no sheet (live): detected={d.Detected} confidence={d.Confidence:0.00}");
        Assert.True(!d.Detected || d.Confidence < LiveCameraThreshold);
    }

    [Fact]
    public void A_table_with_clutter_but_no_sheet_is_not_reported_as_a_sheet()
    {
        // Wood grain, a dark slab and a pen; nothing shaped like a sheet.
        var far = new Quad(new PointD(-5000, -5000), new PointD(-4900, -5000), new PointD(-4900, -4900), new PointD(-5000, -4900));
        RgbImage scene = SceneBuilder.Render(800, 600, far, new SceneBuilder.Options((150, 105, 70), Sheet, Stripes: true, Clutter: true, Noise: 6, Seed: 4));

        QuadDetection d = new DocumentEdgeDetector { Trace = output.WriteLine }.Detect(scene);

        output.WriteLine($"no sheet: detected={d.Detected} confidence={d.Confidence:0.00}");
        Assert.False(d.Detected);
    }

    [Fact]
    public void Soft_shadow_on_a_light_desk_does_not_pull_the_outline_to_the_frame()
    {
        Quad truth = Q(250, 45, 590, 60, 700, 555, 105, 530);
        RgbImage scene = SceneBuilder.Render(800, 600, truth, new SceneBuilder.Options((205, 198, 186), Sheet, Shadow: 0.2, DropShadow: 14, Noise: 4));
        QuadDetection d = new DocumentEdgeDetector { Trace = output.WriteLine }.Detect(scene);
        (double err, double iou) = SceneBuilder.Score(truth, d.Quad, 800, 600);
        output.WriteLine($"IoU={iou:0.000} err={err:P1}");
        Assert.True(iou >= 0.92 && err <= 0.03, $"IoU {iou:0.000}, corner error {err:P1}");
    }

    [Fact]
    public void Very_low_contrast_sheet_is_still_outlined()
    {
        Quad truth = Q(150, 60, 650, 70, 640, 540, 160, 530);
        RgbImage scene = SceneBuilder.Render(800, 600, truth, new SceneBuilder.Options((215, 212, 205), (236, 233, 226), Noise: 2));
        QuadDetection d = new DocumentEdgeDetector { Trace = output.WriteLine }.Detect(scene);
        (double err, double iou) = SceneBuilder.Score(truth, d.Quad, 800, 600);
        output.WriteLine($"IoU={iou:0.000} err={err:P1}");
        Assert.True(iou >= 0.92 && err <= 0.03, $"IoU {iou:0.000}, corner error {err:P1}");
    }

    [Fact]
    public void A_blank_wall_falls_back_to_the_whole_frame()
    {
        var img = new RgbImage(640, 480);
        var rnd = new Random(3);
        for (int i = 0; i < img.Data.Length; i++) img.Data[i] = (byte)(150 + rnd.Next(-3, 4));

        QuadDetection d = new DocumentEdgeDetector().Detect(img);

        Assert.False(d.Detected);
        Assert.Equal(Quad.Inset(0.03), d.Quad);
    }

    [Fact]
    public void Corners_come_back_in_TL_TR_BR_BL_order()
    {
        Quad truth = Q(150, 60, 650, 70, 640, 540, 160, 530);
        QuadDetection d = new DocumentEdgeDetector().Detect(SceneBuilder.Render(800, 600, truth, new SceneBuilder.Options(DarkDesk, Sheet)));
        Assert.True(d.Detected);
        Assert.True(d.Quad.TopLeft.X < d.Quad.TopRight.X && d.Quad.BottomLeft.X < d.Quad.BottomRight.X);
        Assert.True(d.Quad.TopLeft.Y < d.Quad.BottomLeft.Y && d.Quad.TopRight.Y < d.Quad.BottomRight.Y);
        Assert.True(d.Quad.IsConvex);
    }

    [Fact]
    public void Detection_is_fast_enough_for_a_phone()
    {
        Quad truth = Q(150, 60, 650, 70, 640, 540, 160, 530);
        RgbImage scene = SceneBuilder.Render(1600, 1200, truth.Scale(2, 2), new SceneBuilder.Options(DarkDesk, Sheet, Noise: 4));
        var detector = new DocumentEdgeDetector();
        detector.Detect(scene); // warm-up (JIT)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        QuadDetection d = detector.Detect(scene);
        sw.Stop();
        output.WriteLine($"1600x1200 detection: {sw.ElapsedMilliseconds} ms");
        Assert.True(d.Detected);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds} ms");
    }
}

public class HomographyTests
{
    [Fact]
    public void Maps_the_source_points_onto_the_targets_and_inverts()
    {
        var src = new[] { new PointD(0, 0), new PointD(100, 0), new PointD(100, 200), new PointD(0, 200) };
        var dst = new[] { new PointD(30, 20), new PointD(180, 40), new PointD(160, 260), new PointD(10, 230) };
        Homography h = Homography.FromPoints(src, dst);
        for (int i = 0; i < 4; i++)
        {
            PointD p = h.Apply(src[i].X, src[i].Y);
            Assert.Equal(dst[i].X, p.X, 6);
            Assert.Equal(dst[i].Y, p.Y, 6);
        }
        PointD back = h.Inverse().Apply(dst[2].X, dst[2].Y);
        Assert.Equal(100, back.X, 6);
        Assert.Equal(200, back.Y, 6);
        PointD mid = h.Inverse().Apply(h.Apply(37, 91).X, h.Apply(37, 91).Y);
        Assert.Equal(37, mid.X, 6);
        Assert.Equal(91, mid.Y, 6);
    }

    [Fact]
    public void Degenerate_points_throw()
    {
        var src = new[] { new PointD(0, 0), new PointD(1, 1), new PointD(2, 2), new PointD(3, 3) }; // collinear
        Assert.Throws<InvalidOperationException>(() => Homography.FromPoints(src, src));
    }

    [Fact]
    public void Quad_geometry_helpers()
    {
        Quad unit = Quad.Full;
        Assert.Equal(1.0, unit.Area, 9);
        Assert.True(unit.IsConvex);
        Assert.True(unit.Contains(0.5, 0.5));
        Assert.False(unit.Contains(1.5, 0.5));
        var bowtie = new Quad(new PointD(0, 0), new PointD(1, 1), new PointD(1, 0), new PointD(0, 1));
        Assert.False(bowtie.IsConvex);
        Assert.Equal(unit, Quad.FromValues(unit.ToValues()));
    }
}
