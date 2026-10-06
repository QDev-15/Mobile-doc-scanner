using DocScanner.Core;
using ImageCoreService;

namespace DocScanner.Core.Tests;

/// <summary>Placeholder pages while photos are copied in, and the result screen's live preview / brightness / contrast.</summary>
public class PlaceholderAndPreviewTests
{
    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly DocumentStore Store;
        public readonly FakeImageService Images = new();
        public readonly PageIngestQueue Queue;
        public readonly ImportService Import;
        public readonly PageEditService Edit;
        public readonly CropRenderService Render;

        public Rig()
        {
            Store = new DocumentStore(Root.Path);
            var detection = new CropDetectionService(Store, Images, new FakeEdgeDetector(() => new QuadDetection(Quad.Inset(0.1), 0.8, true)));
            Render = new CropRenderService(Store, Images);
            Queue = new PageIngestQueue(Store, Images, detection, 2, Render);
            Import = new ImportService(Store, Queue);
            Edit = new PageEditService(Store, Queue, detection);
        }

        /// <summary>A freshly imported page, forced to <see cref="PageColorMode.Color"/>: a new import defaults to
        /// black-and-white (owner's request 2026-09-29), but most tests using this helper are specifically about the
        /// color / preview pipeline and set their own filter right after when they need something else.</summary>
        public async Task<(string DocId, PageRecord Page)> OneReadyPage()
        {
            DocumentRecord doc = Store.Create();
            await Import.ImportAsync(doc, [new ImportSource("a.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))]);
            await Queue.WaitIdleAsync();
            PageRecord page = Store.Pages(doc.Id).Single();
            Edit.SetFilter(doc.Id, page.Id, PageColorMode.Color);
            return (doc.Id, Store.Pages(doc.Id).Single());
        }

        public void Dispose() => Root.Dispose();
    }

    private sealed class Recorder : IImportObserver
    {
        public readonly List<(string PageId, double Fraction)> Progress = [];
        public readonly List<string> Changed = [];
        public void CopyProgress(string pageId, double fraction) { lock (Progress) Progress.Add((pageId, fraction)); }
        public void PageChanged(string pageId) { lock (Changed) Changed.Add(pageId); }
    }

    [Fact]
    public async Task Placeholders_keep_their_place_and_report_the_copy_as_it_goes()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        IReadOnlyList<string> ids = rig.Import.AddPlaceholders(doc.Id, 3);
        Assert.Equal(ids, rig.Store.Pages(doc.Id).Select(p => p.Id));

        var big = new byte[3_000_000]; // copied in 256 KB steps: several progress reports
        var recorder = new Recorder();
        await rig.Import.FillAsync(doc.Id, ids,
        [
            new ImportSource("a.jpg", _ => Task.FromResult<Stream>(new MemoryStream(big))),
            new ImportSource("b.jpg", _ => throw new IOException("unreadable")),
            new ImportSource("c.png", _ => Task.FromResult<Stream>(new MemoryStream([1]))),
        ], observer: recorder);

        IReadOnlyList<PageRecord> pages = rig.Store.Pages(doc.Id);
        Assert.Equal(ids, pages.Select(p => p.Id));                  // same pages, same order: filled in place
        Assert.NotEqual(PageState.Importing, pages[0].State);
        Assert.Equal(PageState.Failed, pages[1].State);               // the unreadable one stays where it was
        Assert.Equal(".png", pages[2].OriginalExtension);
        Assert.Equal(ids, recorder.Changed);

        double[] first = recorder.Progress.Where(p => p.PageId == ids[0]).Select(p => p.Fraction).ToArray();
        Assert.True(first.Length >= 5);
        Assert.Equal(first.OrderBy(f => f), first);                   // only forward
        Assert.Equal(1.0, first[^1]);
        await rig.Queue.WaitIdleAsync();
    }

    [Fact]
    public async Task A_deleted_placeholder_is_skipped_and_the_rest_are_filled()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        IReadOnlyList<string> ids = rig.Import.AddPlaceholders(doc.Id, 3);
        rig.Store.TrashPage(doc.Id, ids[1]);

        ImportSource Ok(string n) => new(n, _ => Task.FromResult<Stream>(new MemoryStream([1, 2])));
        ImportResult r = await rig.Import.FillAsync(doc.Id, ids, [Ok("1.jpg"), Ok("2.jpg"), Ok("3.jpg")]);

        Assert.Equal(2, r.Added);
        Assert.Equal([ids[0], ids[2]], rig.Store.Pages(doc.Id).Select(p => p.Id));
        Assert.False(Directory.Exists(rig.Store.PageFolder(doc.Id, ids[1])));
        await rig.Queue.WaitIdleAsync();
    }

    [Fact]
    public void Placeholders_left_by_a_killed_app_are_removed_at_the_next_start()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        rig.Import.AddPlaceholders(doc.Id, 4);

        var restarted = new DocumentStore(rig.Root.Path); // the next run reads the saved document
        Assert.Equal(4, restarted.Pages(doc.Id).Count);
        Assert.Equal(4, restarted.RemoveUnfinishedImports(doc.Id));
        Assert.Empty(restarted.Pages(doc.Id));
        Assert.Empty(new DocumentStore(rig.Root.Path).Pages(doc.Id));
        Assert.Equal(0, restarted.RemoveUnfinishedImports(doc.Id));
    }

    [Fact]
    public async Task Undoing_the_deletion_of_a_placeholder_being_copied_brings_it_back_as_failed()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        string id = rig.Import.AddPlaceholders(doc.Id, 1).Single();
        Directory.CreateDirectory(rig.Store.PageFolder(doc.Id, id)); // its photo was being copied
        DeletedPage deleted = rig.Store.TrashPage(doc.Id, id)!;

        Assert.True(rig.Store.RestorePage(deleted));
        PageRecord p = rig.Store.Pages(doc.Id).Single();
        Assert.Equal(PageState.Failed, p.State); // not a placeholder nobody will ever fill
        Assert.NotNull(p.Error);
        await rig.Queue.WaitIdleAsync();
    }

    [Fact]
    public async Task The_preview_has_the_saved_pages_shape_at_screen_size()
    {
        using var rig = new Rig();
        rig.Images.Raw = Gradient(4000, 3000);
        (string docId, PageRecord page) = await rig.OneReadyPage();
        rig.Edit.SetFilter(docId, page.Id, PageColorMode.Gray, cleanBackground: false); // a filtered render: straightened again
        rig.Queue.EnqueueRender(docId, page.Id);
        await rig.Queue.WaitIdleAsync();
        page = rig.Store.Pages(docId).Single();
        Assert.False(page.HasPlainColorRender);

        int regions = rig.Images.Regions.Count;
        RgbImage preview = (await rig.Render.RenderPreviewAsync(docId, page.Id))!;
        Assert.Equal(regions + 1, rig.Images.Regions.Count);
        Assert.Equal(CropPlanner.PreviewLongEdge, Math.Max(preview.Width, preview.Height));
        double savedRatio = (double)page.CroppedWidth / page.CroppedHeight, previewRatio = (double)preview.Width / preview.Height;
        Assert.InRange(previewRatio, savedRatio * 0.99, savedRatio * 1.01);
        Assert.True(Math.Max(page.CroppedWidth, page.CroppedHeight) > CropPlanner.PreviewLongEdge); // the saved page stays full size
    }

    [Fact]
    public async Task A_plain_color_render_is_reused_as_the_preview_instead_of_straightening_the_photo_again()
    {
        using var rig = new Rig();
        rig.Images.Raw = Gradient(4000, 3000);
        (string docId, PageRecord page) = await rig.OneReadyPage();
        rig.Queue.EnqueueRender(docId, page.Id); // color, neutral: what the background prerender makes after import
        await rig.Queue.WaitIdleAsync();
        page = rig.Store.Pages(docId).Single();
        Assert.True(page.HasPlainColorRender);

        int regions = rig.Images.Regions.Count;
        Assert.NotNull(await rig.Render.RenderPreviewAsync(docId, page.Id));
        Assert.Equal(regions, rig.Images.Regions.Count);                    // the photo was not decoded again
        Assert.Equal(CropPlanner.PreviewLongEdge, rig.Images.LoadMaxEdges[^1]); // the saved page was read, at screen size

        // Anything that changes the straightened page or filters it makes the render unusable as the plain preview.
        rig.Edit.SetFilter(docId, page.Id, brightness: 20);
        rig.Queue.EnqueueRender(docId, page.Id);
        await rig.Queue.WaitIdleAsync();
        Assert.False(rig.Store.Pages(docId).Single().HasPlainColorRender);
        rig.Edit.SetFilter(docId, page.Id, brightness: 0);
        rig.Queue.EnqueueRender(docId, page.Id);
        await rig.Queue.WaitIdleAsync();
        Assert.True(rig.Store.Pages(docId).Single().HasPlainColorRender);
        rig.Edit.RotateOutput(docId, page.Id, 90);
        Assert.False(rig.Store.Pages(docId).Single().HasPlainColorRender); // not re-rendered yet
        rig.Edit.SetCrop(docId, page.Id, Quad.Inset(0.2));
        Assert.False(rig.Store.Pages(docId).Single().HasPlainColorRender);
    }

    [Fact]
    public async Task No_preview_for_a_page_that_is_not_ready()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        string id = rig.Import.AddPlaceholders(doc.Id, 1).Single();
        Assert.Null(await rig.Render.RenderPreviewAsync(doc.Id, id));
    }

    [Fact]
    public async Task Brightness_and_contrast_make_a_color_page_stale_and_are_baked_into_the_saved_page()
    {
        using var rig = new Rig();
        rig.Images.Raw = Flat(4000, 3000, 100); // the fake reports every original as 4000 x 3000
        (string docId, PageRecord page) = await rig.OneReadyPage();
        rig.Queue.EnqueueRender(docId, page.Id);
        await rig.Queue.WaitIdleAsync();
        Assert.False(rig.Store.Pages(docId).Single().NeedsRender);

        Assert.True(rig.Edit.SetFilter(docId, page.Id, brightness: 30, contrast: 20));
        page = rig.Store.Pages(docId).Single();
        Assert.True(page.NeedsRender);

        rig.Queue.EnqueueRender(docId, page.Id);
        await rig.Queue.WaitIdleAsync();
        page = rig.Store.Pages(docId).Single();
        Assert.False(page.NeedsRender);
        Assert.Equal((30, 20), (page.CroppedBrightness, page.CroppedContrast));
        RgbImage saved = rig.Images.Saved[rig.Store.CroppedPath(docId, page)];
        byte expected = new ToneAdjust(30, 20).Lut()[100];
        Assert.Equal(expected, saved.Data[saved.Data.Length / 2]);

        // Black and white uses the brightness (it moves the threshold) but not the contrast.
        rig.Edit.SetFilter(docId, page.Id, PageColorMode.BlackWhite);
        rig.Queue.EnqueueRender(docId, page.Id);
        await rig.Queue.WaitIdleAsync();
        rig.Edit.SetFilter(docId, page.Id, contrast: -50);
        Assert.False(rig.Store.Pages(docId).Single().NeedsRender);
        rig.Edit.SetFilter(docId, page.Id, brightness: -50);
        Assert.True(rig.Store.Pages(docId).Single().NeedsRender);
    }

    [Fact]
    public async Task Applying_a_look_to_all_pages_includes_brightness_and_contrast()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        ImportSource Ok(string n) => new(n, _ => Task.FromResult<Stream>(new MemoryStream([1])));
        await rig.Import.ImportAsync(doc, [Ok("1.jpg"), Ok("2.jpg")]);
        await rig.Queue.WaitIdleAsync();

        IReadOnlyList<string> changed = rig.Edit.ApplyFilterToAll(doc.Id, new FilterOptions(PageColorMode.Color) { Tone = new ToneAdjust(10, -5) });
        Assert.Equal(2, changed.Count);
        Assert.All(rig.Store.Pages(doc.Id), p => Assert.Equal((10, -5), (p.Brightness, p.Contrast)));
    }

    private static RgbImage Flat(int w, int h, byte v)
    {
        var img = new RgbImage(w, h);
        Array.Fill(img.Data, v);
        return img;
    }

    private static RgbImage Gradient(int w, int h)
    {
        var img = new RgbImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                img.Data[o] = (byte)(x * 255 / w);
                img.Data[o + 1] = (byte)(y * 255 / h);
                img.Data[o + 2] = 128;
            }
        return img;
    }
}

/// <summary>Turning the finished page on the result screen: the photo and the outline stay, the saved page is turned.</summary>
public class OutputRotationTests
{
    [Fact]
    public async Task Turning_the_page_turns_the_saved_page_and_its_preview_without_touching_the_photo()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        var images = new FakeImageService();
        var detection = new CropDetectionService(store, images, new FakeEdgeDetector(() => new QuadDetection(Quad.Inset(0.1), 0.8, true)));
        var render = new CropRenderService(store, images);
        var queue = new PageIngestQueue(store, images, detection, 2, render);
        var edit = new PageEditService(store, queue, detection);
        images.Raw = new RgbImage(4000, 3000);
        // Gray without cleaning: the preview must be straightened from the photo (a plain color render would be reused
        // instead), and gray is still an exact function of each pixel, so turned renders can be compared pixel for pixel.
        new Random(3).NextBytes(images.Raw.Data);

        DocumentRecord doc = store.Create();
        await new ImportService(store, queue).ImportAsync(doc, [new ImportSource("a.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1])))]);
        await queue.WaitIdleAsync();
        string id = store.Pages(doc.Id).Single().Id;
        edit.SetFilter(doc.Id, id, PageColorMode.Gray, cleanBackground: false);
        queue.EnqueueRender(doc.Id, id);
        await queue.WaitIdleAsync();
        PageRecord before = store.Pages(doc.Id).Single();
        RgbImage straight = images.Saved[store.CroppedPath(doc.Id, before)];
        double[] quad = before.CropQuad!;
        (int oldW, int oldH) = (before.CroppedWidth, before.CroppedHeight); // before is the live record: copy its values
        RgbImage previewBefore = (await render.RenderPreviewAsync(doc.Id, id))!;

        Assert.True(edit.RotateOutput(doc.Id, id, -90)); // counter-clockwise = 270
        PageRecord p = store.Pages(doc.Id).Single();
        Assert.Equal(270, p.OutputRotation);
        Assert.Equal(PageState.Ready, p.State);          // stays editable: nothing but the render is redone
        Assert.Equal(quad, p.CropQuad);
        Assert.Equal(0, p.UserRotation);
        Assert.True(p.NeedsRender);

        queue.EnqueueRender(doc.Id, id);
        await queue.WaitIdleAsync();
        p = store.Pages(doc.Id).Single();
        Assert.False(p.NeedsRender);                    // and does not look like a stretched A4 render
        Assert.Equal((oldH, oldW), (p.CroppedWidth, p.CroppedHeight));
        Assert.Equal(straight.RotateClockwise(3).Data, images.Saved[store.CroppedPath(doc.Id, p)].Data);

        RgbImage previewAfter = (await render.RenderPreviewAsync(doc.Id, id))!;
        Assert.Equal(previewBefore.RotateClockwise(3).Data, previewAfter.Data);

        edit.RotateOutput(doc.Id, id, 90);
        Assert.Equal(0, store.Pages(doc.Id).Single().OutputRotation);
    }
}
