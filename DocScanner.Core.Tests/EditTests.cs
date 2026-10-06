using DocScanner.Core;
using ImageCoreService;

namespace DocScanner.Core.Tests;

public class RotationGeometryTests
{
    [Theory]
    // exif, extra degrees clockwise, expected effective exif
    [InlineData(1, 0, 1)]
    [InlineData(1, 90, 6)]
    [InlineData(1, 180, 3)]
    [InlineData(1, 270, 8)]
    [InlineData(6, 90, 3)]
    [InlineData(6, 270, 1)]
    [InlineData(3, 90, 8)]
    [InlineData(8, 90, 1)]
    [InlineData(2, 90, 7)]   // a mirrored photo turned clockwise stays mirrored: 2 -> 7
    [InlineData(5, 90, 2)]
    [InlineData(1, 360, 1)]
    [InlineData(1, -90, 8)]
    [InlineData(99, 0, 1)]   // garbage tag is treated as "as stored"
    public void Rotation_composes_with_the_exif_orientation(int exif, int degrees, int expected) =>
        Assert.Equal(expected, ImageGeometry.ComposeRotation(exif, degrees));

    [Fact]
    public void Four_quarter_turns_are_the_identity_for_every_orientation()
    {
        for (int o = 1; o <= 8; o++)
        {
            int r = o;
            for (int i = 0; i < 4; i++) r = ImageGeometry.ComposeRotation(r, 90);
            Assert.Equal(o, r);
        }
    }

    [Fact]
    public void Composed_orientation_gives_the_same_size_swap_as_turning_the_picture()
    {
        // 4000x3000 stored; EXIF 6 makes it 3000x4000; a further quarter turn brings it back to 4000x3000.
        int o = ImageGeometry.ComposeRotation(6, 90);
        Assert.Equal((4000, 3000), ImageGeometry.UprightSize(4000, 3000, o));
    }

    [Fact]
    public void Rotating_a_quad_a_quarter_turn_keeps_corner_order_and_returns_after_four_turns()
    {
        var q = new Quad(new PointD(0.1, 0.2), new PointD(0.8, 0.1), new PointD(0.9, 0.9), new PointD(0.2, 0.8));

        Quad r = ImageGeometry.RotateQuadClockwise(q);

        // old TL (0.1,0.2) -> (0.8,0.1) and is now the top-right corner
        Assert.Equal(new PointD(0.8, 0.1), r.TopRight);
        Assert.True(r.IsConvex);
        Assert.True(r.TopLeft.X < r.TopRight.X && r.BottomLeft.X < r.BottomRight.X);
        Assert.True(r.TopLeft.Y < r.BottomLeft.Y && r.TopRight.Y < r.BottomRight.Y);

        Quad back = q;
        for (int i = 0; i < 4; i++) back = ImageGeometry.RotateQuadClockwise(back);
        Assert.Equal(q.ToValues().Select(v => Math.Round(v, 9)), back.ToValues().Select(v => Math.Round(v, 9)));
        Assert.Equal(q.Area, r.Area, 9);
    }
}

public class PageEditServiceTests
{
    private static readonly Quad Detected = new(new PointD(0.1, 0.1), new PointD(0.9, 0.12), new PointD(0.88, 0.9), new PointD(0.12, 0.92));

    private sealed class Rig : IDisposable
    {
        public TempRoot Root = new();
        public DocumentStore Store;
        public FakeImageService Images = new();
        public PageIngestQueue Queue;
        public PageEditService Edit;
        public ImportService Import;
        public int DetectorCalls;

        public Rig()
        {
            Store = new DocumentStore(Root.Path);
            var detection = new CropDetectionService(Store, Images, new FakeEdgeDetector(() =>
            {
                DetectorCalls++;
                return new QuadDetection(Detected, 0.8, true);
            }));
            Queue = new PageIngestQueue(Store, Images, detection);
            Edit = new PageEditService(Store, Queue, detection);
            Import = new ImportService(Store, Queue);
        }

        public async Task<(DocumentRecord Doc, PageRecord Page)> ImportOnePage()
        {
            DocumentRecord doc = Store.Create();
            await Import.ImportAsync(doc, [new ImportSource("a.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))]);
            await Queue.WaitIdleAsync();
            return (doc, Store.Pages(doc.Id).Single());
        }

        public void Dispose() => Root.Dispose();
    }

    [Fact]
    public async Task A_hand_made_outline_is_saved_and_survives_a_reopen()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.ImportOnePage();
        var mine = new Quad(new PointD(0.2, 0.2), new PointD(0.7, 0.25), new PointD(0.65, 0.8), new PointD(0.15, 0.75));

        Assert.True(rig.Edit.SetCrop(doc.Id, page.Id, mine));

        PageRecord back = new DocumentStore(rig.Root.Path).Get(doc.Id)!.Pages[0];
        Assert.Equal(mine, Quad.FromValues(back.CropQuad!));
        Assert.True(back.CropManual);
    }

    [Fact]
    public async Task Automatic_detection_does_not_overwrite_a_hand_made_outline_but_the_auto_button_does()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.ImportOnePage();
        rig.Edit.SetCrop(doc.Id, page.Id, Quad.Inset(0.25));

        // the background detector finishing late (or running again) must not undo the user's work
        var detection = new CropDetectionService(rig.Store, rig.Images, new FakeEdgeDetector(() => new QuadDetection(Detected, 0.8, true)));
        await detection.DetectAsync(doc.Id, page.Id);
        Assert.Equal(Quad.Inset(0.25), Quad.FromValues(rig.Store.Pages(doc.Id)[0].CropQuad!));

        // ... but pressing "Tự động" replaces it and clears the manual flag
        await rig.Edit.RedetectAsync(doc.Id, page.Id);
        PageRecord after = rig.Store.Pages(doc.Id)[0];
        Assert.Equal(Detected, Quad.FromValues(after.CropQuad!));
        Assert.False(after.CropManual);
    }

    [Fact]
    public async Task Full_image_sets_the_whole_frame()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.ImportOnePage();

        rig.Edit.UseFullImage(doc.Id, page.Id);

        Assert.Equal(Quad.Full, Quad.FromValues(rig.Store.Pages(doc.Id)[0].CropQuad!));
    }

    [Fact]
    public async Task Rotate_turns_the_outline_rebuilds_the_files_with_the_new_orientation_and_keeps_the_outline()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.ImportOnePage(); // fake photo: 4000x3000, EXIF 6 -> upright 3000x4000
        Quad before = Quad.FromValues(page.CropQuad!);
        Assert.Equal((1200, 1600), (page.ProxyWidth, page.ProxyHeight));
        int detectorCallsBefore = rig.DetectorCalls;

        Assert.True(rig.Edit.Rotate(doc.Id, page.Id));
        Assert.Equal(PageState.Pending, rig.Store.Pages(doc.Id)[0].State); // it is being rebuilt
        await rig.Queue.WaitIdleAsync();

        PageRecord p = rig.Store.Pages(doc.Id)[0];
        Assert.Equal(PageState.Ready, p.State);
        Assert.Equal(90, p.UserRotation);
        Assert.Equal(6, p.ExifOrientation);                                  // the file's own tag is untouched
        Assert.Equal(3, p.EffectiveOrientation);                             // 6 + a quarter turn = upside down
        Assert.Equal((4000, 3000), p.UprightSize);
        Assert.Equal((1600, 1200), (p.ProxyWidth, p.ProxyHeight));
        Assert.Equal(new[] { 90 }, rig.Images.ThumbRotations.Skip(1).ToArray()); // the rebuild passed the user rotation
        Assert.Equal(3, rig.Images.ProxyOrientations.Last());
        Assert.Equal(ImageGeometry.RotateQuadClockwise(before), Quad.FromValues(p.CropQuad!));
        Assert.Equal(detectorCallsBefore, rig.DetectorCalls);               // no new detection: the outline was turned instead
    }

    [Fact]
    public async Task Four_rotations_come_back_to_the_start()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.ImportOnePage();
        Quad before = Quad.FromValues(page.CropQuad!);

        for (int i = 0; i < 4; i++)
        {
            Assert.True(rig.Edit.Rotate(doc.Id, page.Id));
            await rig.Queue.WaitIdleAsync();
        }

        PageRecord p = rig.Store.Pages(doc.Id)[0];
        Assert.Equal(0, p.UserRotation);
        Assert.Equal(6, p.EffectiveOrientation);
        Assert.Equal(before.ToValues().Select(v => Math.Round(v, 9)), Quad.FromValues(p.CropQuad!).ToValues().Select(v => Math.Round(v, 9)));
    }

    [Fact]
    public async Task A_page_that_is_still_being_prepared_cannot_be_rotated()
    {
        using var rig = new Rig();
        rig.Images.Gate = new TaskCompletionSource();
        DocumentRecord doc = rig.Store.Create();
        await rig.Import.ImportAsync(doc, [new ImportSource("a.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1])))]);
        PageRecord page = rig.Store.Pages(doc.Id).Single();

        Assert.False(rig.Edit.Rotate(doc.Id, page.Id)); // Pending: a rotation now would race the thumbnail job

        rig.Images.Gate.SetResult();
        await rig.Queue.WaitIdleAsync();
        Assert.Equal(0, rig.Store.Pages(doc.Id).Single().UserRotation);
    }

    [Fact]
    public async Task A_detection_started_before_a_rotation_does_not_overwrite_the_turned_outline()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.ImportOnePage();
        rig.Store.Update(doc.Id, d => d.Pages[0].CropQuad = null); // pretend detection is still to come

        // A slow detector: the page is rotated while it is thinking.
        var gate = new TaskCompletionSource();
        var slow = new CropDetectionService(rig.Store, rig.Images, new FakeEdgeDetector(() => { gate.Task.Wait(); return new QuadDetection(Detected, 0.8, true); }));
        Task<QuadDetection?> running = slow.DetectAsync(doc.Id, page.Id);
        await Task.Delay(50);
        Assert.True(rig.Edit.Rotate(doc.Id, page.Id));
        await rig.Queue.WaitIdleAsync();
        gate.SetResult();
        await running;

        // the stale result (for the old orientation) was dropped; the rotated page got its own detection
        PageRecord p = rig.Store.Pages(doc.Id)[0];
        Assert.Equal(90, p.UserRotation);
        Assert.NotNull(p.CropQuad);
    }
}
