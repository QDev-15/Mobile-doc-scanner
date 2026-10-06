using DocScanner.Core;
using ImageCoreService;

namespace DocScanner.Core.Tests;

public class OrientationMappingTests
{
    // Independent of the production matrices: where does the stored pixel (x, y) of a W x H file end up
    // when the file is displayed with EXIF orientation o? (the standard table)
    private static (int X, int Y) Displayed(int o, int x, int y, int w, int h) => o switch
    {
        1 => (x, y),
        2 => (w - 1 - x, y),
        3 => (w - 1 - x, h - 1 - y),
        4 => (x, h - 1 - y),
        5 => (y, x),
        6 => (h - 1 - y, x),
        7 => (h - 1 - y, w - 1 - x),
        8 => (y, w - 1 - x),
        _ => throw new ArgumentOutOfRangeException(nameof(o)),
    };

    /// <summary>The file a camera would write so that showing it with orientation <paramref name="o"/> gives <paramref name="upright"/>.</summary>
    public static RgbImage MakeStored(RgbImage upright, int o)
    {
        bool swap = ImageGeometry.IsTransposed(o);
        int sw = swap ? upright.Height : upright.Width, sh = swap ? upright.Width : upright.Height;
        var stored = new RgbImage(sw, sh);
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                (int dx, int dy) = Displayed(o, x, y, sw, sh);
                Array.Copy(upright.Data, (dy * upright.Width + dx) * 3, stored.Data, (y * sw + x) * 3, 3);
            }
        return stored;
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void An_outline_on_the_upright_picture_lands_on_the_same_pixels_of_the_stored_file(int o)
    {
        // A pixel of the upright picture and the stored pixel that shows there must match under UprightToStored.
        const int w = 40, h = 30; // stored dims
        bool swap = ImageGeometry.IsTransposed(o);
        int uw = swap ? h : w, uh = swap ? w : h;
        foreach ((int sx, int sy) in new[] { (0, 0), (39, 0), (39, 29), (0, 29), (5, 17) })
        {
            (int dx, int dy) = Displayed(o, sx, sy, w, h);
            // pixel centres, normalized on each side
            var upright = new PointD((dx + 0.5) / uw, (dy + 0.5) / uh);
            var q = new Quad(upright, upright, upright, upright);

            PointD back = ImageGeometry.UprightToStored(q, o).TopLeft;

            Assert.Equal((sx + 0.5) / w, back.X, 9);
            Assert.Equal((sy + 0.5) / h, back.Y, 9);
        }
    }
}

public class CropPlannerTests
{
    private static Quad Rect(double w, double h, double x = 0, double y = 0) =>
        new(new PointD(x, y), new PointD(x + w, y), new PointD(x + w, y + h), new PointD(x, y + h));

    [Fact]
    public void A_small_page_is_decoded_at_full_resolution_and_kept_at_full_size()
    {
        CropPlan p = CropPlanner.Plan(Rect(1000, 1400, 100, 100), 3000, 2000, CropAspect.Free);
        Assert.Equal(1, p.Sample);
        Assert.Equal((1000, 1400), (p.OutWidth, p.OutHeight));
        Assert.True(p.RegionX <= 100 && p.RegionX + p.RegionWidth >= 1100);
        Assert.True(p.RegionY <= 100 && p.RegionY + p.RegionHeight >= 1500);
    }

    [Fact]
    public void A_12_megapixel_photo_is_capped_at_a4_300_dpi_without_shrinking_the_decode()
    {
        CropPlan p = CropPlanner.Plan(Rect(3800, 2800, 100, 100), 4000, 3000, CropAspect.Free);
        Assert.Equal(1, p.Sample);
        // 3800 x 2800 = 10.6 MP: shrunk to the 8.7 MP budget (which bites before the 3508 px long-edge cap)
        Assert.True((long)p.OutWidth * p.OutHeight <= CropPlanner.MaxPixels);
        Assert.InRange(Math.Max(p.OutWidth, p.OutHeight), 3400, 3508);
    }

    [Fact]
    public void A_48_megapixel_photo_is_decoded_at_half_scale_and_only_where_the_page_is()
    {
        CropPlan p = CropPlanner.Plan(Rect(7000, 5000, 500, 400), 8000, 6000, CropAspect.Free);
        Assert.Equal(2, p.Sample);
        Assert.InRange((long)p.OutWidth * p.OutHeight, 8_000_000, CropPlanner.MaxPixels + 10_000);
        Assert.True((long)p.RegionWidth * p.RegionHeight / (p.Sample * p.Sample) <= CropPlanner.MaxDecodePixels);
        Assert.True(p.RegionWidth < 8000 && p.RegionHeight < 6000); // the margins of the photo are not decoded
    }

    [Fact]
    public void A_huge_photo_shrinks_the_decode_to_fit_memory_and_the_output_follows()
    {
        CropPlan p = CropPlanner.Plan(Rect(19000, 14000, 500, 500), 20000, 15000, CropAspect.Free);
        Assert.Equal(8, p.Sample);
        Assert.True((double)p.RegionWidth / p.Sample * p.RegionHeight / p.Sample <= CropPlanner.MaxDecodePixels);
        // output cannot be sharper than the decoded pixels: about 1/8 of the original resolution
        Assert.InRange(p.OutWidth, 2300, 2400);
    }

    [Fact]
    public void A4_pages_have_the_standard_ratio_portrait_or_landscape_following_the_outline()
    {
        CropPlan portrait = CropPlanner.Plan(Rect(1000, 1400, 100, 100), 3000, 2000);   // an A4 sheet seen front on, taller than wide
        Assert.True(portrait.OutHeight > portrait.OutWidth);
        Assert.Equal(1.41421, (double)portrait.OutHeight / portrait.OutWidth, 2);

        CropPlan landscape = CropPlanner.Plan(Rect(1400, 1000, 100, 100), 3000, 2000);  // wider than tall
        Assert.True(landscape.OutWidth > landscape.OutHeight);
        Assert.Equal(1.41421, (double)landscape.OutWidth / landscape.OutHeight, 2);

        // A letter page (1.3) is a sheet: A4 in A4 mode; "Theo khung" keeps its own shape.
        CropPlan letter = CropPlanner.Plan(Rect(1000, 1300, 100, 100), 3000, 2000);
        Assert.Equal(1.41421, (double)letter.OutHeight / letter.OutWidth, 2);
        CropPlan free = CropPlanner.Plan(Rect(1000, 1300, 100, 100), 3000, 2000, CropAspect.Free);
        Assert.Equal(1.3, (double)free.OutHeight / free.OutWidth, 2);
        // A receipt (3 : 1) is not: it keeps its shape even in A4 mode.
        CropPlan receipt = CropPlanner.Plan(Rect(500, 1500, 100, 100), 3000, 2000);
        Assert.Equal(3.0, (double)receipt.OutHeight / receipt.OutWidth, 1);
    }

    [Fact]
    public void A_big_A4_page_is_capped_at_300_dpi_and_keeps_its_ratio()
    {
        CropPlan p = CropPlanner.Plan(Rect(3200, 4400, 100, 100), 3400, 4600);
        Assert.InRange(p.OutHeight, 3490, 3508);
        Assert.InRange(p.OutWidth, 2465, 2482);
        Assert.True((long)p.OutWidth * p.OutHeight <= CropPlanner.MaxPixels + 12_000);
        Assert.Equal(1.41421, (double)p.OutHeight / p.OutWidth, 2);
    }

    [Fact]
    public void An_A4_page_from_a_48_megapixel_photo_is_still_decoded_at_half_scale()
    {
        CropPlan p = CropPlanner.Plan(Rect(5000, 7000, 500, 200), 6000, 8000);
        Assert.Equal(2, p.Sample);
        Assert.Equal(1.41421, (double)p.OutHeight / p.OutWidth, 2);
        Assert.InRange(p.OutHeight, 3480, 3508);
    }

    [Fact]
    public void The_region_never_leaves_the_photo()
    {
        CropPlan p = CropPlanner.Plan(Rect(3000, 2000, 0, 0), 3000, 2000, CropAspect.Free);
        Assert.Equal((0, 0), (p.RegionX, p.RegionY));
        Assert.True(p.RegionX + p.RegionWidth <= 3000 && p.RegionY + p.RegionHeight <= 2000);
    }
}

public class CropRenderTests
{
    /// <summary>An upright picture with no symmetry (colour gradients + a chequer), so any flip / turn shows.</summary>
    private static RgbImage Upright(int w, int h)
    {
        var img = new RgbImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                img.Data[o] = (byte)(x * 255 / (w - 1));
                img.Data[o + 1] = (byte)(y * 255 / (h - 1));
                img.Data[o + 2] = (byte)(((x / 20 + y / 20) & 1) * 200);
            }
        return img;
    }

    private static double MeanAbsError(RgbImage a, RgbImage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        long sum = 0;
        for (int i = 0; i < a.Data.Length; i++) sum += Math.Abs(a.Data[i] - b.Data[i]);
        return (double)sum / a.Data.Length;
    }

    private static RgbImage Crop(RgbImage src, int x, int y, int w, int h)
    {
        var c = new RgbImage(w, h);
        for (int yy = 0; yy < h; yy++) Array.Copy(src.Data, ((y + yy) * src.Width + x) * 3, c.Data, yy * w * 3, w * 3);
        return c;
    }

    [Theory]
    // file's EXIF tag, user rotation
    [InlineData(1, 0)] [InlineData(2, 0)] [InlineData(3, 0)] [InlineData(4, 0)]
    [InlineData(5, 0)] [InlineData(6, 0)] [InlineData(7, 0)] [InlineData(8, 0)]
    [InlineData(1, 90)] [InlineData(6, 90)] [InlineData(3, 270)] [InlineData(2, 180)]
    public async Task The_straightened_page_is_the_outlined_part_of_the_upright_picture_for_every_orientation(int exif, int userRotation)
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        var images = new FakeImageService();
        var render = new CropRenderService(store, images);

        // What the user sees (upright) and the file the camera wrote for it.
        RgbImage upright = Upright(400, 300);
        int effective = ImageGeometry.ComposeRotation(exif, userRotation);
        RgbImage stored = OrientationMappingTests.MakeStored(upright, effective);
        images.Raw = stored;

        DocumentRecord doc = store.Create();
        // outline on the upright picture: x 100..300, y 60..270
        var quad = new Quad(new PointD(0.25, 0.2), new PointD(0.75, 0.2), new PointD(0.75, 0.9), new PointD(0.25, 0.9));
        store.Update(doc.Id, d => d.Pages.Add(new PageRecord
        {
            Id = "p1", State = PageState.Ready, CleanBackground = false, FreeAspect = true, RawWidth = stored.Width, RawHeight = stored.Height,
            ExifOrientation = exif, UserRotation = userRotation, CropQuad = quad.ToValues(),
        }));

        await render.RenderAsync(doc.Id, "p1");

        PageRecord page = store.Pages(doc.Id).Single();
        Assert.Equal(1, page.CroppedRevision);
        Assert.Equal((200, 210), (page.CroppedWidth, page.CroppedHeight));
        RgbImage flat = images.Saved[store.CroppedPath(doc.Id, "p1", 1)];
        double mae = MeanAbsError(Crop(upright, 100, 60, 200, 210), flat);
        Assert.True(mae < 1.5, $"exif {exif} + rotation {userRotation}: mean abs error {mae:0.00} (a wrong orientation gives 40+)");
        Assert.True(File.Exists(store.CroppedThumbPath(doc.Id, "p1", 1)));
        Assert.False(page.NeedsRender);
    }

    [Fact]
    public async Task A_tilted_outline_is_straightened_from_a_rotated_file()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        var images = new FakeImageService();
        RgbImage upright = Upright(400, 300);
        images.Raw = OrientationMappingTests.MakeStored(upright, 6);
        DocumentRecord doc = store.Create();
        // a slanted quadrilateral inside the upright picture
        var quad = new Quad(new PointD(0.2, 0.15), new PointD(0.8, 0.25), new PointD(0.75, 0.85), new PointD(0.25, 0.75));
        store.Update(doc.Id, d => d.Pages.Add(new PageRecord
        {
            Id = "p1", State = PageState.Ready, CleanBackground = false, FreeAspect = true, RawWidth = images.Raw.Width, RawHeight = images.Raw.Height,
            ExifOrientation = 6, CropQuad = quad.ToValues(),
        }));

        await new CropRenderService(store, images).RenderAsync(doc.Id, "p1");

        // Corners of the straightened page come from the corners of the outline: check the four colours.
        RgbImage flat = images.Saved[store.CroppedPath(doc.Id, "p1", 1)];
        void Corner(int fx, int fy, double ux, double uy)
        {
            int o = (fy * flat.Width + fx) * 3;
            Assert.InRange(flat.Data[o], ux * 255 - 14, ux * 255 + 14);       // red follows x of the upright picture
            Assert.InRange(flat.Data[o + 1], uy * 255 - 14, uy * 255 + 14);   // green follows y
        }
        Corner(2, 2, 0.2, 0.15);
        Corner(flat.Width - 3, 2, 0.8, 0.25);
        Corner(flat.Width - 3, flat.Height - 3, 0.75, 0.85);
        Corner(2, flat.Height - 3, 0.25, 0.75);
    }

    [Fact]
    public async Task By_default_the_straightened_page_is_A4_and_the_free_option_keeps_the_outline_proportions()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        var images = new FakeImageService();
        RgbImage upright = Upright(400, 300);
        images.Raw = upright;
        DocumentRecord doc = store.Create();
        // outline x 100..300, y 20..290: 200 x 270, sheet-shaped (1 : 1.35) -> snapped to A4
        var quad = new Quad(new PointD(0.25, 20 / 300.0), new PointD(0.75, 20 / 300.0), new PointD(0.75, 290 / 300.0), new PointD(0.25, 290 / 300.0));
        store.Update(doc.Id, d => d.Pages.Add(new PageRecord
        {
            Id = "p1", State = PageState.Ready, CleanBackground = false, RawWidth = 400, RawHeight = 300, CropQuad = quad.ToValues(),
        }));
        var render = new CropRenderService(store, images);

        await render.RenderAsync(doc.Id, "p1");

        PageRecord page = store.Pages(doc.Id).Single();
        Assert.Equal((200, 283), (page.CroppedWidth, page.CroppedHeight)); // A4 portrait, no resolution lost
        RgbImage a4 = images.Saved[store.CroppedPath(doc.Id, "p1", 1)];
        // the outline's corners are still the page's corners (the picture is just stretched to A4)
        Assert.InRange(a4.Data[(1 * 200 + 1) * 3], 100 * 255 / 399 - 6, 100 * 255 / 399 + 8);            // top-left: red of x=100
        int br = ((a4.Height - 2) * 200 + (a4.Width - 2)) * 3;
        Assert.InRange(a4.Data[br], 300 * 255 / 399 - 8, 300 * 255 / 399 + 6);                           // bottom-right: red of x=300
        Assert.InRange(a4.Data[br + 1], 290 * 255 / 299 - 8, 290 * 255 / 299 + 6);                       // green of y=290
        Assert.False(page.CroppedFreeAspect);

        // switching to the outline's own proportions makes the render stale and gives 200 x 270
        store.Update(doc.Id, d => d.Pages[0].FreeAspect = true);
        Assert.True(store.Pages(doc.Id).Single().NeedsRender);
        await render.RenderAsync(doc.Id, "p1");
        page = store.Pages(doc.Id).Single();
        Assert.Equal((200, 270), (page.CroppedWidth, page.CroppedHeight));
        Assert.True(page.CroppedFreeAspect);
        Assert.False(page.NeedsRender);
    }

    [Fact]
    public async Task An_outline_that_is_not_sheet_shaped_is_not_stretched_to_A4()
    {
        // Owner's page 7 (2026-09-26): a sheet cut off by the photo frame, outline nearly square. Forcing A4
        // stretched the text; now it keeps its proportions (the PDF export centers it on an A4 page).
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        var images = new FakeImageService { Raw = Upright(400, 300) };
        DocumentRecord doc = store.Create();
        var quad = new Quad(new PointD(0.25, 0.2), new PointD(0.75, 0.2), new PointD(0.75, 0.9), new PointD(0.25, 0.9)); // 200 x 210
        store.Update(doc.Id, d => d.Pages.Add(new PageRecord
        {
            Id = "p1", State = PageState.Ready, CleanBackground = false, RawWidth = 400, RawHeight = 300, CropQuad = quad.ToValues(),
        }));

        await new CropRenderService(store, images).RenderAsync(doc.Id, "p1");

        PageRecord page = store.Pages(doc.Id).Single();
        Assert.Equal((200, 210), (page.CroppedWidth, page.CroppedHeight));
        Assert.False(page.FreeAspect);        // still the user's A4 choice ...
        Assert.False(page.NeedsRender);       // ... and the render counts as current for it
    }

    [Fact]
    public async Task A_sheet_cut_by_the_frame_keeps_its_true_outline_and_the_missing_part_is_white()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        var images = new FakeImageService();
        RgbImage upright = Upright(400, 300);
        images.Raw = upright;
        DocumentRecord doc = store.Create();
        // the sheet's left corners are 10 % of the width off the left edge of the photo: x -40 .. 200
        var quad = new Quad(new PointD(-0.1, 0.2), new PointD(0.5, 0.2), new PointD(0.5, 0.9), new PointD(-0.1, 0.9));
        store.Update(doc.Id, d => d.Pages.Add(new PageRecord
        {
            Id = "p1", State = PageState.Ready, CleanBackground = false, FreeAspect = true, RawWidth = 400, RawHeight = 300, CropQuad = quad.ToValues(),
        }));

        await new CropRenderService(store, images).RenderAsync(doc.Id, "p1");

        RgbImage flat = images.Saved[store.CroppedPath(doc.Id, "p1", 1)];
        Assert.Equal((240, 210), (flat.Width, flat.Height));   // the true 240 x 210 outline, not clipped to the photo
        for (int y = 5; y < 205; y += 50)
            for (int x = 2; x < 36; x += 8)                    // the 40 px that were never photographed
                Assert.Equal(255, flat.Data[(y * 240 + x) * 3]);
        // the photographed part is the crop x 0..200, y 60..270, starting at output column 40
        double err = 0;
        for (int y = 0; y < 210; y++)
            for (int x = 45; x < 240; x++)
                err += Math.Abs(flat.Data[(y * 240 + x) * 3 + 1] - upright.Data[((60 + y) * 400 + (x - 40)) * 3 + 1]);
        Assert.True(err / (210.0 * 195) < 2, $"mean abs error {err / (210.0 * 195):0.00}");
    }

    [Fact]
    public async Task Only_the_part_of_the_photo_holding_the_page_is_requested()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        var images = new FakeImageService { Raw = Upright(400, 300) };
        DocumentRecord doc = store.Create();
        store.Update(doc.Id, d => d.Pages.Add(new PageRecord
        {
            Id = "p1", State = PageState.Ready, CleanBackground = false, FreeAspect = true, RawWidth = 400, RawHeight = 300,
            CropQuad = new Quad(new PointD(0.25, 0.2), new PointD(0.75, 0.2), new PointD(0.75, 0.9), new PointD(0.25, 0.9)).ToValues(),
        }));

        await new CropRenderService(store, images).RenderAsync(doc.Id, "p1");

        (int x, int y, int w, int h, int sample) = images.Regions.Single();
        Assert.Equal(1, sample);
        Assert.InRange(x, 90, 100);
        Assert.InRange(w, 200, 215);
        Assert.InRange(h, 210, 225);
    }
}

public class RenderStageTests
{
    private sealed class Rig : IDisposable
    {
        public TempRoot Root = new();
        public DocumentStore Store;
        public FakeImageService Images = new();
        public PageIngestQueue Queue;
        public PageEditService Edit;
        public ImportService Import;

        public Rig()
        {
            Store = new DocumentStore(Root.Path);
            var detection = new CropDetectionService(Store, Images, new FakeEdgeDetector(() =>
                new QuadDetection(new Quad(new PointD(0.1, 0.1), new PointD(0.9, 0.1), new PointD(0.9, 0.9), new PointD(0.1, 0.9)), 0.8, true)));
            var render = new CropRenderService(Store, Images);
            Queue = new PageIngestQueue(Store, Images, detection, 2, render);
            Edit = new PageEditService(Store, Queue, detection);
            Import = new ImportService(Store, Queue);
        }

        /// <summary>A freshly imported page, forced to <see cref="PageColorMode.Color"/>: a new import defaults to
        /// black-and-white (owner's request 2026-09-29), but most tests using this helper are about the render
        /// pipeline's mechanics (staleness, file replacement...), not about color mode, and are written against a
        /// color page (checking for a ".jpg" render, in particular).</summary>
        public async Task<(DocumentRecord Doc, PageRecord Page)> OnePage()
        {
            DocumentRecord doc = Store.Create();
            await Import.ImportAsync(doc, [new ImportSource("a.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))]);
            await Queue.WaitIdleAsync();
            PageRecord page = Store.Pages(doc.Id).Single();
            Edit.SetFilter(doc.Id, page.Id, PageColorMode.Color);
            return (doc, Store.Pages(doc.Id).Single());
        }

        public void Dispose() => Root.Dispose();
    }

    [Fact]
    public async Task Render_makes_the_straightened_page_and_is_skipped_when_nothing_changed()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.OnePage();
        Assert.True(page.NeedsRender);

        rig.Queue.EnqueueRender(doc.Id, page.Id);
        await rig.Queue.WaitIdleAsync();
        PageRecord p = rig.Store.Pages(doc.Id).Single();
        Assert.Equal(1, p.CroppedRevision);
        Assert.False(p.NeedsRender);
        Assert.True(File.Exists(rig.Store.CroppedPath(doc.Id, page.Id, 1)));

        int regionsBefore = rig.Images.Regions.Count;
        rig.Queue.EnqueueRender(doc.Id, page.Id); // nothing changed since: no second render
        await rig.Queue.WaitIdleAsync();
        Assert.Equal(regionsBefore, rig.Images.Regions.Count);
        Assert.Equal(1, rig.Store.Pages(doc.Id).Single().CroppedRevision);
    }

    [Fact]
    public async Task Editing_the_outline_or_rotating_makes_the_render_stale_and_a_new_render_replaces_the_old_files()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.OnePage();
        rig.Queue.EnqueueRender(doc.Id, page.Id);
        await rig.Queue.WaitIdleAsync();
        string first = rig.Store.CroppedPath(doc.Id, page.Id, 1);

        rig.Edit.SetCrop(doc.Id, page.Id, Quad.Inset(0.2));
        Assert.True(rig.Store.Pages(doc.Id).Single().NeedsRender);
        rig.Queue.EnqueueRender(doc.Id, page.Id);
        await rig.Queue.WaitIdleAsync();

        PageRecord p = rig.Store.Pages(doc.Id).Single();
        Assert.Equal(2, p.CroppedRevision);
        Assert.False(p.NeedsRender);
        Assert.False(File.Exists(first));                                          // the old render is gone
        Assert.False(File.Exists(rig.Store.CroppedThumbPath(doc.Id, page.Id, 1)));
        Assert.True(File.Exists(rig.Store.CroppedPath(doc.Id, page.Id, 2)));

        Assert.True(rig.Edit.Rotate(doc.Id, page.Id));
        await rig.Queue.WaitIdleAsync();
        Assert.True(rig.Store.Pages(doc.Id).Single().NeedsRender);                 // rotation also invalidates it
    }

    [Fact]
    public async Task A_render_that_fails_leaves_the_page_usable_and_records_why()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.OnePage();
        File.Delete(rig.Store.OriginalPath(doc.Id, page)); // FakeImageService does not read it, so break the region loader instead
        var broken = new BrokenRegionImages(rig.Images);
        var render = new CropRenderService(rig.Store, broken);
        var queue = new PageIngestQueue(rig.Store, broken, null, 1, render);

        queue.EnqueueRender(doc.Id, page.Id);
        await queue.WaitIdleAsync();

        PageRecord p = rig.Store.Pages(doc.Id).Single();
        Assert.Equal(PageState.Ready, p.State);
        Assert.Equal(0, p.CroppedRevision);
        Assert.Equal("cannot decode region", p.RenderError);
    }

    [Fact]
    public async Task Black_and_white_renders_a_one_bit_png_and_switching_back_to_color_replaces_it()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.OnePage();
        rig.Queue.EnqueueRender(doc.Id, page.Id);
        await rig.Queue.WaitIdleAsync();
        string colorJpg = rig.Store.CroppedPath(doc.Id, rig.Store.Pages(doc.Id).Single());
        Assert.EndsWith(".jpg", colorJpg);

        Assert.True(rig.Edit.SetFilter(doc.Id, page.Id, PageColorMode.BlackWhite));
        Assert.True(rig.Store.Pages(doc.Id).Single().NeedsRender);
        rig.Queue.EnqueueRender(doc.Id, page.Id);
        await rig.Queue.WaitIdleAsync();

        PageRecord p = rig.Store.Pages(doc.Id).Single();
        Assert.False(p.NeedsRender);
        Assert.Equal((".png", PageColorMode.BlackWhite), (p.CroppedExtension, p.CroppedColorMode));
        string png = rig.Store.CroppedPath(doc.Id, p);
        PngReader.PngData data = PngReader.Read(File.ReadAllBytes(png));
        Assert.Equal((p.CroppedWidth, p.CroppedHeight, 8), (data.Width, data.Height, data.BitDepth)); // gray: anti-aliased edges
        Assert.False(File.Exists(colorJpg));                                       // previous render (other extension) removed
        Assert.True(File.Exists(rig.Store.CroppedThumbPath(doc.Id, p.Id, p.CroppedRevision)));

        rig.Edit.SetFilter(doc.Id, page.Id, PageColorMode.Color);
        rig.Queue.EnqueueRender(doc.Id, page.Id);
        await rig.Queue.WaitIdleAsync();
        p = rig.Store.Pages(doc.Id).Single();
        Assert.Equal(".jpg", p.CroppedExtension);
        Assert.False(File.Exists(png));
    }

    [Fact]
    public async Task Gray_mode_saves_a_gray_jpeg()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.OnePage();
        rig.Edit.SetFilter(doc.Id, page.Id, PageColorMode.Gray);
        rig.Queue.EnqueueRender(doc.Id, page.Id);
        await rig.Queue.WaitIdleAsync();

        PageRecord p = rig.Store.Pages(doc.Id).Single();
        RgbImage saved = rig.Images.Saved[rig.Store.CroppedPath(doc.Id, p)];
        for (int i = 0; i < saved.Data.Length; i += 3)
            Assert.True(saved.Data[i] == saved.Data[i + 1] && saved.Data[i + 1] == saved.Data[i + 2]);
    }

    [Fact]
    public async Task Only_settings_that_change_the_picture_make_the_render_stale()
    {
        using var rig = new Rig();
        (DocumentRecord doc, PageRecord page) = await rig.OnePage();
        rig.Queue.EnqueueRender(doc.Id, page.Id);
        await rig.Queue.WaitIdleAsync();

        rig.Edit.SetFilter(doc.Id, page.Id, darkness: 90, cleanBackground: false); // color page: neither matters
        Assert.False(rig.Store.Pages(doc.Id).Single().NeedsRender);
        rig.Edit.SetFilter(doc.Id, page.Id, cleanBackground: true);

        rig.Edit.SetFilter(doc.Id, page.Id, PageColorMode.Gray);
        rig.Queue.EnqueueRender(doc.Id, page.Id);
        await rig.Queue.WaitIdleAsync();
        rig.Edit.SetFilter(doc.Id, page.Id, darkness: 10);                        // gray: darkness does not matter
        Assert.False(rig.Store.Pages(doc.Id).Single().NeedsRender);
        rig.Edit.SetFilter(doc.Id, page.Id, cleanBackground: false);              // ... background cleaning does
        Assert.True(rig.Store.Pages(doc.Id).Single().NeedsRender);
    }

    [Fact]
    public async Task Applying_a_look_to_all_pages_reports_only_the_pages_that_change()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        await rig.Import.ImportAsync(doc, Enumerable.Range(0, 3)
            .Select(i => new ImportSource($"{i}.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, (byte)i])))).ToList());
        await rig.Queue.WaitIdleAsync();
        string first = rig.Store.Pages(doc.Id)[0].Id;
        rig.Edit.SetFilter(doc.Id, first, PageColorMode.BlackWhite, darkness: 70);

        IReadOnlyList<string> changed = rig.Edit.ApplyFilterToAll(doc.Id, new FilterOptions(PageColorMode.BlackWhite, 70));
        Assert.Equal(2, changed.Count);
        Assert.DoesNotContain(first, changed);
        Assert.All(rig.Store.Pages(doc.Id), p => Assert.Equal((PageColorMode.BlackWhite, 70), (p.ColorMode, p.BwDarkness)));
    }

    [Fact]
    public void A_document_saved_before_page_looks_existed_still_reads_as_rendered_color_pages()
    {
        using var root = new TempRoot();
        string id = "0123456789abcdef0123456789abcdef";
        Directory.CreateDirectory(Path.Combine(root.Path, id));
        File.WriteAllText(Path.Combine(root.Path, id, "doc.json"), $$"""
            { "id": "{{id}}", "name": "old", "pages": [ { "id": "p1", "state": "Ready", "rawWidth": 100, "rawHeight": 80,
              "cropQuad": [0,0,1,0,1,1,0,1], "croppedRevision": 1, "croppedQuad": [0,0,1,0,1,1,0,1], "croppedWidth": 10, "croppedHeight": 14 } ] }
            """);
        PageRecord p = new DocumentStore(root.Path).Pages(id).Single();
        Assert.Equal(PageColorMode.Color, p.ColorMode);
        // It needs one new render, though: it was straightened by the old rules (no true proportions, no curved sides).
        Assert.True(p.RenderIsOutdated);
        Assert.True(p.NeedsRender);
        p.CroppedGeometry = PageRecord.GeometryVersion;
        Assert.False(p.NeedsRender); // the look itself (color, no settings) matched: nothing else to redo
        Assert.EndsWith("cropped_1.jpg", new DocumentStore(root.Path).CroppedPath(id, p));
    }

    private sealed class BrokenRegionImages(FakeImageService inner) : IImageService
    {
        public Task<ImageInfo> CreateThumbAsync(string o, string t, int e, int r, CancellationToken ct) => inner.CreateThumbAsync(o, t, e, r, ct);
        public Task CreateProxyAsync(string o, string p, int e, int or, CancellationToken ct) => inner.CreateProxyAsync(o, p, e, or, ct);
        public Task<RgbImage> LoadRgbAsync(string p, int m, CancellationToken ct) => inner.LoadRgbAsync(p, m, ct);
        public Task<RgbImage> LoadRegionAsync(string o, int x, int y, int w, int h, int s, CancellationToken ct) =>
            throw new InvalidDataException("cannot decode region");
        public Task SaveJpegAsync(RgbImage i, string p, int q, CancellationToken ct) => inner.SaveJpegAsync(i, p, q, ct);
    }
}
