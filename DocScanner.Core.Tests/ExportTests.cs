using DocScanner.Core;
using ImageCoreService;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocScanner.Core.Tests;

public class ExportTests
{
    private sealed class Rig : IDisposable
    {
        public TempRoot Root = new();
        public DocumentStore Store;
        public FakeImageService Images = new();
        public PageIngestQueue Queue;
        public PageEditService Edit;
        public ImportService Import;
        public PdfExportService Export;

        public Rig()
        {
            Store = new DocumentStore(Root.Path);
            var detection = new CropDetectionService(Store, Images, new FakeEdgeDetector(() =>
                new QuadDetection(new Quad(new PointD(0.1, 0.1), new PointD(0.9, 0.1), new PointD(0.9, 0.9), new PointD(0.1, 0.9)), 0.8, true)));
            Queue = new PageIngestQueue(Store, Images, detection, 2, new CropRenderService(Store, Images));
            Edit = new PageEditService(Store, Queue, detection);
            Import = new ImportService(Store, Queue);
            Export = new PdfExportService(Store, Queue, Images);
        }

        public async Task<DocumentRecord> DocWithPages(int count, string name = "Hợp đồng 03/2026")
        {
            DocumentRecord doc = Store.Create(name);
            await Import.ImportAsync(doc, Enumerable.Range(0, count)
                .Select(i => new ImportSource($"{i}.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, (byte)i])))).ToList());
            await Queue.WaitIdleAsync();
            return doc;
        }

        public string OutPath(string file = "out.pdf") => Path.Combine(Root.Path, "exports", file);

        public void Dispose() => Root.Dispose();
    }

    [Fact]
    public async Task Exports_every_page_in_order_rendering_the_ones_not_yet_straightened()
    {
        using var rig = new Rig();
        DocumentRecord doc = await rig.DocWithPages(3);
        IReadOnlyList<PageRecord> pages = rig.Store.Pages(doc.Id);
        rig.Edit.SetFilter(doc.Id, pages[0].Id, PageColorMode.Color); // a new import defaults to black-and-white now
        rig.Edit.SetFilter(doc.Id, pages[1].Id, PageColorMode.BlackWhite);
        rig.Edit.SetFreeAspect(doc.Id, pages[2].Id, true);
        Assert.All(rig.Store.Pages(doc.Id), p => Assert.True(p.NeedsRender)); // nothing rendered yet

        var stages = new List<ExportProgress>();
        PdfExportResult result = await rig.Export.ExportAsync(doc.Id, rig.OutPath(),
            new InlineProgress<ExportProgress>(stages.Add));

        Assert.Equal(3, result.PageCount);
        Assert.Empty(result.SkippedPages);
        Assert.Contains(stages, s => s.Stage == "Đang ghi PDF" && s.Done == 3);
        Assert.Contains(stages, s => s.Stage == "Đang nén trang");
        Assert.All(rig.Store.Pages(doc.Id), p => Assert.False(p.NeedsRender));

        using PdfDocument pdf = PdfDocument.Open(File.ReadAllBytes(result.Path));
        Assert.Equal(3, pdf.NumberOfPages);
        Assert.Equal("Hợp đồng 03/2026", pdf.Information.Title);

        // Fake photo: 4000x3000 with EXIF 6 -> upright 3000x4000 (portrait); outline 80% x 80%.
        Page p1 = pdf.GetPage(1), p2 = pdf.GetPage(2), p3 = pdf.GetPage(3);
        Assert.Equal(595.276, p1.Width, 1);
        Assert.Equal(841.890, p1.Height, 1);
        Assert.Equal(595.276, p2.Width, 1);
        Assert.Equal(841.890 * 0.75, p3.Width, 0);                         // free aspect keeps the outline's 3:4
        Assert.Equal(841.890, p3.Height, 1);

        IPdfImage img1 = p1.GetImages().Single();
        Assert.True(img1.ImageDictionary.TryGet(UglyToad.PdfPig.Tokens.NameToken.Filter, out UglyToad.PdfPig.Tokens.NameToken filter));
        Assert.Equal("DCTDecode", filter.Data);
        PageRecord r1 = rig.Store.Pages(doc.Id)[0];
        // Color pages are re-encoded at the default (Medium: 200 DPI, JPEG 72) from the full-quality render; the fake
        // decoder hands back 64x48, so that is what went into the PDF.
        Assert.Equal((64, 48), (img1.WidthInSamples, img1.HeightInSamples));
        Assert.Equal(PdfQuality.Medium.LongEdgePx, rig.Images.LoadMaxEdges.Last());
        Assert.Contains(rig.Images.Qualities, kv => kv.Key.Contains(".work_") && kv.Value == 72);
        Assert.True(File.Exists(rig.Store.CroppedPath(doc.Id, r1)));                    // the page kept in the app is untouched
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(result.Path)!, ".work_*")); // temp files cleaned up

        // The black-and-white page (kept in the app with anti-aliased edges, 8-bit gray) is shrunk to "Vừa"'s
        // black-and-white target (150 DPI) and stays anti-aliased at "Vừa": a hard 1-bit edge showed a visible pixel
        // staircase once a page was zoomed past its own resolution (owner's report 2026-09-28c), which shrinking a
        // 1-bit page cannot fix, only keeping the soft edge does.
        byte[] kept = File.ReadAllBytes(rig.Store.CroppedPath(doc.Id, rig.Store.Pages(doc.Id)[1]));
        PngReader.PngData keptPng = PngReader.Read(kept);
        Assert.Equal(8, keptPng.BitDepth);
        GrayImage keptGray = PngReader.DecodeGray8(kept);
        int grayEdge = PdfQuality.Medium.GrayLongEdgePx!.Value;
        Assert.True(Math.Max(keptGray.Width, keptGray.Height) > grayEdge, "the fake page should be large enough that Vừa actually shrinks it");
        double scale = (double)grayEdge / Math.Max(keptGray.Width, keptGray.Height);
        GrayImage expectedGray = keptGray.Resize(Math.Max(1, (int)Math.Round(keptGray.Width * scale)), Math.Max(1, (int)Math.Round(keptGray.Height * scale)));
        PngReader.PngData expected = PngReader.Read(PngWriter.EncodeGray8(expectedGray));

        IPdfImage img2 = p2.GetImages().Single();
        Assert.Equal(8, img2.BitsPerComponent);
        Assert.Equal((expectedGray.Width, expectedGray.Height), (img2.WidthInSamples, img2.HeightInSamples));
        Assert.Equal(expected.ZlibData, img2.RawBytes.ToArray());
        Assert.True(img2.TryGetBytesAsMemory(out Memory<byte> bits), "Flate + PNG predictor must decode");
        Assert.Equal((long)img2.WidthInSamples * img2.HeightInSamples, bits.Length);
    }

    [Fact]
    public async Task High_quality_keeps_the_anti_aliased_black_and_white_page()
    {
        using var rig = new Rig();
        DocumentRecord doc = await rig.DocWithPages(1);
        rig.Edit.SetFilter(doc.Id, rig.Store.Pages(doc.Id)[0].Id, PageColorMode.BlackWhite);
        PdfExportResult result = await rig.Export.ExportAsync(doc.Id, rig.OutPath(), null, default, PdfQuality.High);

        byte[] kept = File.ReadAllBytes(rig.Store.CroppedPath(doc.Id, rig.Store.Pages(doc.Id)[0]));
        using PdfDocument pdf = PdfDocument.Open(File.ReadAllBytes(result.Path));
        IPdfImage img = pdf.GetPage(1).GetImages().Single();
        Assert.Equal(8, img.BitsPerComponent);
        Assert.Equal(PngReader.Read(kept).ZlibData, img.RawBytes.ToArray()); // embedded as it is
    }

    /// <summary>Small ("Nhỏ · gửi Zalo, email") has no black-and-white target and keeps the old 1-bit embedding: the
    /// tier is explicitly for a size limit, and losing the smooth edge is the accepted trade there.</summary>
    [Fact]
    public async Task Small_quality_still_embeds_black_and_white_as_1_bit()
    {
        using var rig = new Rig();
        DocumentRecord doc = await rig.DocWithPages(1);
        rig.Edit.SetFilter(doc.Id, rig.Store.Pages(doc.Id)[0].Id, PageColorMode.BlackWhite);
        PdfExportResult result = await rig.Export.ExportAsync(doc.Id, rig.OutPath(), null, default, PdfQuality.Small);

        byte[] kept = File.ReadAllBytes(rig.Store.CroppedPath(doc.Id, rig.Store.Pages(doc.Id)[0]));
        using PdfDocument pdf = PdfDocument.Open(File.ReadAllBytes(result.Path));
        IPdfImage img = pdf.GetPage(1).GetImages().Single();
        Assert.Equal(1, img.BitsPerComponent);
        Assert.Equal(PngReader.Read(PngWriter.EncodeBilevel(PngReader.DecodeGray8(kept))).ZlibData, img.RawBytes.ToArray());
    }

    [Fact]
    public async Task A_page_that_is_not_A4_shaped_is_centered_on_an_A4_sheet_without_stretching()
    {
        using var rig = new Rig();
        DocumentRecord doc = await rig.DocWithPages(1);
        string pageId = rig.Store.Pages(doc.Id)[0].Id;
        // Upright photo 3000 x 4000; outline 80% wide x 60% tall = 2400 x 2400: a square, e.g. a sheet cut by the frame.
        rig.Edit.SetCrop(doc.Id, pageId, new Quad(new PointD(0.1, 0.1), new PointD(0.9, 0.1), new PointD(0.9, 0.7), new PointD(0.1, 0.7)));

        PdfExportResult result = await rig.Export.ExportAsync(doc.Id, rig.OutPath());

        PageRecord p = rig.Store.Pages(doc.Id)[0];
        Assert.Equal(p.CroppedWidth, p.CroppedHeight);                         // rendered square, not stretched to A4
        using PdfDocument pdf = PdfDocument.Open(File.ReadAllBytes(result.Path));
        Page page = pdf.GetPage(1);
        Assert.Equal((595.276, 841.890), (Math.Round(page.Width, 3), Math.Round(page.Height, 3)));  // still an A4 sheet
        var b = page.GetImages().Single().Bounds;
        Assert.Equal(595.276, b.Width, 1);                                     // full width ...
        Assert.Equal(595.276, b.Height, 1);                                    // ... square, not stretched
        Assert.Equal((841.890 - 595.276) / 2, b.Bottom, 1);                    // centered vertically
    }

    [Fact]
    public async Task Unreadable_photos_are_left_out_and_reported()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create("x");
        await rig.Import.ImportAsync(doc,
        [
            new ImportSource("a.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1]))),
            new ImportSource("b.jpg", _ => Task.FromResult<Stream>(new MemoryStream("BAD"u8.ToArray()))),
            new ImportSource("c.jpg", _ => Task.FromResult<Stream>(new MemoryStream([3]))),
        ]);
        await rig.Queue.WaitIdleAsync();

        PdfExportResult result = await rig.Export.ExportAsync(doc.Id, rig.OutPath());
        Assert.Equal(2, result.PageCount);
        Assert.Equal([2], result.SkippedPages);
        using PdfDocument pdf = PdfDocument.Open(File.ReadAllBytes(result.Path));
        Assert.Equal(2, pdf.NumberOfPages);
    }

    [Fact]
    public async Task A_cancelled_export_leaves_no_file_behind()
    {
        using var rig = new Rig();
        DocumentRecord doc = await rig.DocWithPages(2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Export.ExportAsync(doc.Id, rig.OutPath(), null, cts.Token));
        Assert.False(File.Exists(rig.OutPath()));
        Assert.False(File.Exists(rig.OutPath() + ".partial"));
    }

    [Theory]
    [InlineData("Hóa đơn 03/2026", "Hóa đơn 03-2026.pdf")]
    [InlineData("  a:b*c?  ", "a-b-c-.pdf")]
    [InlineData("...", "Tai lieu.pdf")]
    public void File_names_are_safe_and_keep_Vietnamese(string name, string expected) =>
        Assert.Equal(expected, PdfExportService.FileNameFor(name));

    [Fact]
    public async Task Pages_can_be_reordered_renamed_and_a_deleted_page_restored()
    {
        using var rig = new Rig();
        DocumentRecord doc = await rig.DocWithPages(4);
        string[] ids = rig.Store.Pages(doc.Id).Select(p => p.Id).ToArray();

        Assert.True(rig.Store.MovePage(doc.Id, ids[3], 0));
        Assert.Equal([ids[3], ids[0], ids[1], ids[2]], rig.Store.Pages(doc.Id).Select(p => p.Id));
        Assert.True(rig.Store.SetOrder(doc.Id, ids));                     // undo the move
        Assert.Equal(ids, rig.Store.Pages(doc.Id).Select(p => p.Id));

        DeletedPage? deleted = rig.Store.TrashPage(doc.Id, ids[1]);
        Assert.NotNull(deleted);
        Assert.Equal(3, rig.Store.Pages(doc.Id).Count);
        Assert.False(Directory.Exists(rig.Store.PageFolder(doc.Id, ids[1])));
        Assert.True(rig.Store.RestorePage(deleted));
        Assert.Equal(ids, rig.Store.Pages(doc.Id).Select(p => p.Id));
        Assert.True(File.Exists(rig.Store.ThumbPath(doc.Id, rig.Store.Pages(doc.Id)[1])));

        DeletedPage gone = rig.Store.TrashPage(doc.Id, ids[2])!;
        rig.Store.EmptyTrash(doc.Id);
        Assert.False(rig.Store.RestorePage(gone));                        // trash emptied: no way back

        Assert.True(rig.Store.Rename(doc.Id, "  Biên bản  "));
        Assert.False(rig.Store.Rename(doc.Id, "   "));
        Assert.Equal("Biên bản", new DocumentStore(rig.Root.Path).Get(doc.Id)!.Name); // persisted
    }
}

/// <summary>Reports synchronously (Progress&lt;T&gt; posts to the thread pool and may deliver late).</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly object _lock = new();
    public void Report(T value) { lock (_lock) report(value); }
}

public class ExportLibraryTests
{
    [Fact]
    public void Each_export_gets_its_own_file_and_the_list_is_newest_first()
    {
        using var root = new TempRoot();
        var lib = new ExportLibrary(Path.Combine(root.Path, "exports"));
        var t = new DateTime(2026, 9, 26, 14, 5, 0);

        string a = lib.NewPath("Hợp đồng 03/2026", t);
        Assert.EndsWith("Hợp đồng 03-2026 2026-09-26 14.05.pdf", a);
        File.WriteAllText(a, "a");
        string b = lib.NewPath("Hợp đồng 03/2026", t);                 // same minute: no overwrite
        Assert.EndsWith("Hợp đồng 03-2026 2026-09-26 14.05 (2).pdf", b);
        File.WriteAllText(b, "bb");
        File.SetLastWriteTime(a, t);
        File.SetLastWriteTime(b, t.AddMinutes(1));
        File.WriteAllText(Path.Combine(lib.Folder, "x.pdf.partial"), "unfinished");

        IReadOnlyList<ExportedFile> list = lib.List();
        Assert.Equal([b, a], list.Select(f => f.Path));
        Assert.Equal(2, list[0].Bytes);

        Assert.True(lib.Delete(b));
        Assert.False(lib.Delete(Path.Combine(root.Path, "elsewhere.pdf")));  // only inside the library
        Assert.Single(lib.List());
    }

    [Fact]
    public void An_empty_library_lists_nothing() =>
        Assert.Empty(new ExportLibrary(Path.Combine(Path.GetTempPath(), "none_" + Guid.NewGuid().ToString("N"))).List());
}

public class NavigationTests
{
    [Fact]
    public void Neighbor_walks_the_page_order_and_stops_at_the_ends()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        DocumentRecord doc = store.Create("n");
        store.Update(doc.Id, d => d.Pages.AddRange(new[] { "a", "b", "c" }.Select(id => new PageRecord { Id = id })));

        Assert.Equal(("b", 1, 3), store.Neighbor(doc.Id, "a", +1));
        Assert.Equal(("a", 0, 3), store.Neighbor(doc.Id, "b", -1));
        Assert.Null(store.Neighbor(doc.Id, "c", +1));
        Assert.Null(store.Neighbor(doc.Id, "a", -1));
        Assert.Null(store.Neighbor(doc.Id, "zz", +1));
    }
}

public class ExportQualityTests
{
    [Theory]
    [InlineData("small", 150, 60)]
    [InlineData("medium", 200, 72)]
    [InlineData("high", 300, 90)]
    public void Presets_have_the_documented_resolution_and_jpeg_quality(string key, int dpi, int q)
    {
        PdfQuality p = PdfQuality.FromKey(key);
        Assert.Equal((dpi, q), (p.Dpi, p.JpegQuality));
        Assert.Equal((int)Math.Round(11.69 * dpi), p.LongEdgePx);
    }

    [Fact]
    public void Unknown_or_missing_keys_fall_back_to_medium()
    {
        Assert.Same(PdfQuality.Medium, PdfQuality.FromKey(null));
        Assert.Same(PdfQuality.Medium, PdfQuality.FromKey("huge"));
    }

    /// <summary>Small has no black-and-white target (it stays 1-bit, the smallest a page of text gets); Medium and High
    /// do, so their black-and-white pages stay anti-aliased instead of a hard, staircase edge when zoomed
    /// (2026-09-28c). High's target is its own full resolution: <see cref="PdfExportService"/> keeps that page exactly
    /// as saved rather than resizing it, but the value still holds so callers do not need to special-case it.</summary>
    [Fact]
    public void Small_has_no_black_and_white_target_Medium_and_High_do()
    {
        Assert.Null(PdfQuality.Small.GrayLongEdgePx);
        Assert.Equal((int)Math.Round(11.69 * 150), PdfQuality.Medium.GrayLongEdgePx);
        Assert.Equal(PdfQuality.High.LongEdgePx, PdfQuality.High.GrayLongEdgePx);
    }
}

public class StretchedRenderTests
{
    private static readonly double[] Page7Quad = [0.70, 0.19, 0.71, 0.71, 0.24, 1.04, 0.19, 0.05];

    /// <summary>The owner's page 7 exactly as stored on the phone (2026-09-26): rendered 3344 x 2365 (A4 landscape) from a
    /// nearly square outline of a sheet cut off by the photo frame.</summary>
    private static PageRecord Page7() => new()
    {
        Id = "p7", State = PageState.Ready, RawWidth = 4608, RawHeight = 2592, ExifOrientation = 6, UserRotation = 90,
        CropQuad = Page7Quad, CroppedQuad = Page7Quad, CroppedRotation = 90, CroppedRevision = 8,
        CroppedWidth = 3344, CroppedHeight = 2365,
    };

    [Fact]
    public void Renders_made_by_older_straightening_rules_are_redone_once()
    {
        PageRecord p = Page7(); // no geometry version recorded: side-length proportions, straight sides
        Assert.True(p.RenderIsOutdated);
        Assert.True(p.NeedsRender);
        Assert.False(p.HasPlainColorRender);

        p.CroppedGeometry = PageRecord.GeometryVersion; // re-rendered by the current rules
        Assert.False(p.RenderIsOutdated);
        Assert.False(p.NeedsRender);
    }

    [Fact]
    public void Black_and_white_renders_by_the_old_rules_are_redone_once_and_color_ones_are_not()
    {
        PageRecord p = Page7();
        p.CroppedGeometry = PageRecord.GeometryVersion;
        p.ColorMode = p.CroppedColorMode = PageColorMode.BlackWhite; // plain 1-bit Sauvola: no version recorded
        Assert.True(p.RenderIsOutdated);
        Assert.True(p.NeedsRender);

        p.CroppedBlackWhiteVersion = PageRecord.BlackWhiteVersion;
        Assert.False(p.NeedsRender);

        PageRecord color = Page7();
        color.CroppedGeometry = PageRecord.GeometryVersion; // color page, no black-and-white version: nothing to redo
        Assert.False(color.NeedsRender);
    }

    [Fact]
    public void A_change_of_bends_makes_the_render_stale()
    {
        PageRecord p = Page7();
        p.CroppedGeometry = PageRecord.GeometryVersion;
        p.CropBend = [0.05, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(p.NeedsRender);
        p.CroppedBend = [0.05, 0, 0, 0, 0, 0, 0, 0];
        Assert.False(p.NeedsRender);
        p.CropBend = p.CroppedBend = null;
        Assert.False(p.NeedsRender);
        p.CroppedBend = [0, 0, 0, 0, 0, 0, 0, 0]; // flat = no bends
        Assert.False(p.NeedsRender);
    }}
