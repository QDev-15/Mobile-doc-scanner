using DocScanner.Core;
using ImageCoreService;

namespace DocScanner.Core.Tests;

public sealed class TempRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docscanner_tests_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

/// <summary>Writes tiny files instead of decoding. An original whose content starts with "BAD" fails to
/// decode. Every call is logged (T = thumbnail, P = proxy), and a gate can hold the thumbnail stage.</summary>
public sealed class FakeImageService : IImageService
{
    private readonly object _lock = new();
    public List<string> Calls { get; } = [];
    public TaskCompletionSource? Gate { get; set; }

    public List<int> ThumbRotations { get; } = [];
    public List<int> ProxyOrientations { get; } = [];

    public async Task<ImageInfo> CreateThumbAsync(string originalPath, string thumbPath, int thumbEdge, int userRotationDegrees, CancellationToken ct)
    {
        if (Gate != null) await Gate.Task;
        lock (_lock) ThumbRotations.Add(userRotationDegrees);
        string text = File.ReadAllText(originalPath);
        lock (_lock) Calls.Add("T:" + text);
        if (text.StartsWith("BAD")) throw new InvalidDataException("not an image");
        File.WriteAllText(thumbPath, "thumb");
        return new ImageInfo(4000, 3000, 6);
    }

    public Task CreateProxyAsync(string originalPath, string proxyPath, int proxyEdge, int orientation, CancellationToken ct)
    {
        lock (_lock) ProxyOrientations.Add(orientation);
        lock (_lock) Calls.Add("P:" + File.ReadAllText(originalPath));
        File.WriteAllText(proxyPath, "proxy");
        return Task.CompletedTask;
    }

    /// <summary>maxEdge of every LoadRgbAsync call (the export asks for its target resolution).</summary>
    public List<int> LoadMaxEdges { get; } = [];

    /// <summary>JPEG quality of every save, by path.</summary>
    public Dictionary<string, int> Qualities { get; } = [];

    public Task<RgbImage> LoadRgbAsync(string path, int maxEdge, CancellationToken ct)
    {
        lock (_lock) LoadMaxEdges.Add(maxEdge);
        return Task.FromResult(new RgbImage(64, 48));
    }

    /// <summary>The "original" as stored in the file, when a test needs real pixels (else mid-grey).</summary>
    public RgbImage? Raw { get; set; }

    /// <summary>What was encoded, by path.</summary>
    public Dictionary<string, RgbImage> Saved { get; } = [];

    public List<(int X, int Y, int W, int H, int Sample)> Regions { get; } = [];

    public Task<RgbImage> LoadRegionAsync(string originalPath, int x, int y, int width, int height, int sample, CancellationToken ct)
    {
        lock (_lock) Regions.Add((x, y, width, height, sample));
        int w = Math.Max(1, width / sample), h = Math.Max(1, height / sample);
        var region = new RgbImage(w, h);
        if (Raw == null) { Array.Fill(region.Data, (byte)128); return Task.FromResult(region); }

        // crop, then box-shrink like a sub-sampled decode would
        var crop = new RgbImage(width, height);
        for (int yy = 0; yy < height; yy++)
            Array.Copy(Raw.Data, ((y + yy) * Raw.Width + x) * 3, crop.Data, yy * width * 3, width * 3);
        return Task.FromResult(sample > 1 ? crop.Downscale(sample) : crop);
    }

    /// <summary>SOI + a baseline frame header (size, 3 components) + EOI. Not decodable, but structurally a JPEG.</summary>
    public static byte[] MinimalJpeg(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xC0, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
        1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        0xFF, 0xD9,
    ];

    public Task SaveJpegAsync(RgbImage image, string path, int quality, CancellationToken ct)
    {
        lock (_lock) Qualities[path] = quality;
        lock (_lock) Saved[path] = image;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, MinimalJpeg(image.Width, image.Height)); // just a frame header: enough for the PDF export to read

        return Task.CompletedTask;
    }
}

/// <summary>Returns a fixed outline, or throws.</summary>
public sealed class FakeEdgeDetector(Func<QuadDetection> result) : IEdgeDetector
{
    public QuadDetection Detect(RgbImage image) => result();
}

public class StoreAndImportTests
{
    private static ImportSource Src(string name, string content = "photo") =>
        new(name, _ => Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content))));

    private static readonly Quad SomeQuad = new(new PointD(0.1, 0.1), new PointD(0.9, 0.12), new PointD(0.88, 0.9), new PointD(0.12, 0.92));

    private sealed class Rig : IDisposable
    {
        public TempRoot Root = new();
        public DocumentStore Store;
        public FakeImageService Images = new();
        public PageIngestQueue Queue;
        public ImportService Import;

        public Rig(bool withDetection = false, int workers = 2, Func<QuadDetection>? detector = null)
        {
            Store = new DocumentStore(Root.Path);
            CropDetectionService? detection = withDetection
                ? new CropDetectionService(Store, Images, new FakeEdgeDetector(detector ?? (() => new QuadDetection(SomeQuad, 0.77, true))))
                : null;
            Queue = new PageIngestQueue(Store, Images, detection, workers);
            Import = new ImportService(Store, Queue);
        }

        public void Dispose() => Root.Dispose();
    }

    [Fact]
    public void Document_round_trips_through_disk()
    {
        using var t = new TempRoot();
        var store = new DocumentStore(t.Path);
        DocumentRecord doc = store.Create("Hợp đồng");
        store.Update(doc.Id, d => d.Pages.Add(new PageRecord { Id = "p1", RawWidth = 4000, RawHeight = 3000, ExifOrientation = 6, ProxyWidth = 1200, ProxyHeight = 1600, State = PageState.Preview }));

        DocumentRecord back = new DocumentStore(t.Path).Get(doc.Id)!;
        Assert.Equal("Hợp đồng", back.Name);
        Assert.Single(back.Pages);
        Assert.Equal((3000, 4000), back.Pages[0].UprightSize);
        Assert.Equal(PageState.Preview, back.Pages[0].State);
        Assert.False(File.Exists(Path.Combine(t.Path, doc.Id, "doc.json.tmp")));
    }

    [Fact]
    public void Pages_written_before_states_existed_load_as_ready()
    {
        using var t = new TempRoot();
        string dir = Path.Combine(t.Path, "abc123");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "doc.json"),
            """{"schemaVersion":1,"id":"abc123","name":"old","createdUtc":"2026-09-25T07:16:58Z","pages":[{"id":"p1","originalExtension":".jpg","rawWidth":10,"rawHeight":10,"exifOrientation":1,"proxyWidth":10,"proxyHeight":10}]}""");

        PageRecord page = new DocumentStore(t.Path).Get("abc123")!.Pages[0];

        Assert.Equal(PageState.Ready, page.State);
    }

    [Fact]
    public void The_store_hands_out_one_shared_instance_and_serializes_writers()
    {
        using var t = new TempRoot();
        var store = new DocumentStore(t.Path);
        DocumentRecord doc = store.Create();
        Assert.Same(doc, store.Get(doc.Id));

        Parallel.For(0, 100, i => store.Update(doc.Id, d => d.Pages.Add(new PageRecord { Id = "p" + i })));

        Assert.Equal(100, store.Pages(doc.Id).Count);
        Assert.Equal(100, new DocumentStore(t.Path).Get(doc.Id)!.Pages.Count); // and all of it reached the disk
    }

    [Fact]
    public void List_is_newest_first_and_skips_corrupt_documents()
    {
        using var t = new TempRoot();
        var store = new DocumentStore(t.Path);
        DocumentRecord old = store.Create("old");
        store.Update(old.Id, d => d.CreatedUtc = DateTime.UtcNow.AddDays(-2));
        DocumentRecord recent = store.Create("recent");

        string brokenDir = Path.Combine(t.Path, "deadbeef");
        Directory.CreateDirectory(brokenDir);
        File.WriteAllText(Path.Combine(brokenDir, "doc.json"), "{ not json");

        Assert.Equal(new[] { recent.Id, old.Id }, store.List().Select(d => d.Id).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("..\\x")]
    public void Ids_that_could_escape_the_root_are_rejected(string id)
    {
        using var t = new TempRoot();
        var store = new DocumentStore(t.Path);
        Assert.Throws<ArgumentException>(() => store.DocumentFolder(id));
        Assert.Throws<ArgumentException>(() => store.Delete(id));
    }

    [Fact]
    public async Task Import_returns_with_every_page_already_in_the_document_before_any_decoding()
    {
        using var rig = new Rig();
        rig.Images.Gate = new TaskCompletionSource(); // holds the first (thumbnail) stage
        DocumentRecord doc = rig.Store.Create();

        ImportResult r = await rig.Import.ImportAsync(doc, [Src("a.JPG", "AAA"), Src("b.png", "BBB"), Src("noext", "CCC")]);

        // The decode stage has not run (it is blocked), yet the UI can already list three pages.
        Assert.Equal(3, r.Added);
        Assert.Empty(r.Failures);
        IReadOnlyList<PageRecord> pages = rig.Store.Pages(doc.Id);
        Assert.Equal(3, pages.Count);
        Assert.All(pages, p => Assert.Equal(PageState.Pending, p.State));
        Assert.Equal(new[] { ".jpg", ".png", ".jpg" }, pages.Select(p => p.OriginalExtension).ToArray());
        Assert.Equal("AAA", File.ReadAllText(rig.Store.OriginalPath(doc.Id, pages[0])));
        Assert.Equal(3, new DocumentStore(rig.Root.Path).Get(doc.Id)!.Pages.Count); // and it is on disk

        rig.Images.Gate.SetResult();
        await rig.Queue.WaitIdleAsync();

        Assert.All(rig.Store.Pages(doc.Id), p => Assert.Equal(PageState.Ready, p.State));
    }

    /// <summary>Owner's request 2026-09-29: most scans are documents, not photos, so a newly imported page defaults to
    /// black-and-white instead of color (one tap on the filter cards switches a page that does need color). This is set
    /// explicitly when the page is created (<see cref="ImportService.AddPlaceholders"/>), not through
    /// <see cref="PageRecord.ColorMode"/>'s own default, which stays <see cref="PageColorMode.Color"/> so a doc.json
    /// saved before this field existed still reads as the color page it was actually rendered as.</summary>
    [Fact]
    public async Task A_newly_imported_page_defaults_to_black_and_white()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();

        await rig.Import.ImportAsync(doc, [Src("a.jpg", "AAA")]);

        PageRecord page = rig.Store.Pages(doc.Id).Single();
        Assert.Equal(PageColorMode.BlackWhite, page.ColorMode);
        Assert.Equal(PageColorMode.BlackWhite, page.Filter.Mode);
    }

    [Fact]
    public async Task Every_thumbnail_is_made_before_any_proxy()
    {
        using var rig = new Rig(workers: 1); // one worker makes the order deterministic
        rig.Images.Gate = new TaskCompletionSource(); // the worker waits until the whole batch has been added
        DocumentRecord doc = rig.Store.Create();

        await rig.Import.ImportAsync(doc, [Src("1.jpg", "one"), Src("2.jpg", "two"), Src("3.jpg", "three"), Src("4.jpg", "four")]);
        rig.Images.Gate.SetResult();
        await rig.Queue.WaitIdleAsync();

        string[] kinds = rig.Images.Calls.Select(c => c[0].ToString()).ToArray();
        // T T T T P P P P: the first proxy is never made while a thumbnail is still waiting.
        Assert.Equal(new[] { "T", "T", "T", "T", "P", "P", "P", "P" }, kinds);
    }

    [Fact]
    public async Task Thumbnail_stage_records_size_and_exif_and_the_state_moves_through_preview_to_ready()
    {
        using var rig = new Rig();
        var seen = new List<PageState>();
        rig.Queue.PageUpdated += u => { lock (seen) seen.Add(rig.Store.Pages(u.DocId).First(p => p.Id == u.PageId).State); };
        DocumentRecord doc = rig.Store.Create();

        await rig.Import.ImportAsync(doc, [Src("a.jpg")]);
        await rig.Queue.WaitIdleAsync();

        PageRecord p = rig.Store.Pages(doc.Id).Single();
        Assert.Equal(PageState.Ready, p.State);
        Assert.Equal((3000, 4000), p.UprightSize);                 // 4000x3000 with EXIF 6
        Assert.Equal((1200, 1600), (p.ProxyWidth, p.ProxyHeight)); // fitted to the 1600 px long edge
        Assert.True(File.Exists(rig.Store.ThumbPath(doc.Id, p)));
        Assert.True(File.Exists(rig.Store.ProxyPath(doc.Id, p)));
        Assert.Equal(new[] { PageState.Preview, PageState.Ready }, seen.ToArray());
    }

    [Fact]
    public async Task A_bad_photo_becomes_a_failed_page_and_the_rest_finish()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();

        await rig.Import.ImportAsync(doc, [Src("ok1.jpg", "one"), Src("bad.jpg", "BAD"), Src("ok2.jpg", "two")]);
        await rig.Queue.WaitIdleAsync();

        IReadOnlyList<PageRecord> pages = rig.Store.Pages(doc.Id);
        Assert.Equal(new[] { PageState.Ready, PageState.Failed, PageState.Ready }, pages.Select(p => p.State).ToArray());
        Assert.Equal("not an image", pages[1].Error);
    }

    [Fact]
    public async Task Detected_outlines_are_recorded_after_the_pages_are_ready()
    {
        using var rig = new Rig(withDetection: true);
        DocumentRecord doc = rig.Store.Create();

        await rig.Import.ImportAsync(doc, [Src("a.jpg"), Src("b.jpg")]);
        await rig.Queue.WaitIdleAsync();

        PageRecord page = new DocumentStore(rig.Root.Path).Get(doc.Id)!.Pages[1];
        Assert.Equal(SomeQuad, Quad.FromValues(page.CropQuad!));
        Assert.Equal(0.77, page.CropConfidence);
        Assert.True(page.CropDetected);
    }

    [Fact]
    public async Task A_detector_crash_leaves_a_working_page_without_an_outline()
    {
        using var rig = new Rig(withDetection: true, detector: () => throw new InvalidOperationException("boom"));
        DocumentRecord doc = rig.Store.Create();

        await rig.Import.ImportAsync(doc, [Src("a.jpg")]);
        await rig.Queue.WaitIdleAsync();

        PageRecord page = rig.Store.Pages(doc.Id).Single();
        Assert.Equal(PageState.Ready, page.State);
        Assert.Null(page.CropQuad);
    }

    [Fact]
    public async Task Cancel_keeps_the_pages_already_added_and_drops_the_partial_one()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ImportProgress>(p => { if (p.Done == 2) cts.Cancel(); });

        ImportResult r = await rig.Import.ImportAsync(doc, [Src("1.jpg"), Src("2.jpg"), Src("3.jpg"), Src("4.jpg")], progress, cts.Token);
        await rig.Queue.WaitIdleAsync();

        Assert.True(r.Cancelled);
        Assert.Equal(2, r.Added);
        Assert.Equal(2, rig.Store.Pages(doc.Id).Count);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(rig.Root.Path, doc.Id)).Length);
    }

    [Fact]
    public async Task Each_page_is_saved_as_it_is_added()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        var seen = new List<int>();
        // Placeholders are all saved first; then each page is saved filled as soon as its photo is in.
        var progress = new SyncProgress<ImportProgress>(p => seen.Add(new DocumentStore(rig.Root.Path).Get(doc.Id)!.Pages.Count(x => x.State != PageState.Importing)));

        await rig.Import.ImportAsync(doc, [Src("1.jpg"), Src("2.jpg"), Src("3.jpg")], progress);

        Assert.Equal(new[] { 0, 1, 2, 3 }, seen.ToArray()); // what a crash after each step would have kept
    }

    [Fact]
    public async Task Deleting_a_page_or_document_while_it_is_queued_does_not_break_the_pipeline()
    {
        using var rig = new Rig(withDetection: true);
        rig.Images.Gate = new TaskCompletionSource();
        DocumentRecord doc = rig.Store.Create();
        DocumentRecord other = rig.Store.Create();
        await rig.Import.ImportAsync(doc, [Src("1.jpg", "one"), Src("2.jpg", "two")]);
        await rig.Import.ImportAsync(other, [Src("3.jpg", "three")]);

        rig.Store.DeletePage(doc.Id, rig.Store.Pages(doc.Id)[0].Id);
        rig.Store.Delete(other.Id);
        rig.Images.Gate.SetResult();
        await rig.Queue.WaitIdleAsync();

        Assert.Equal(PageState.Ready, rig.Store.Pages(doc.Id).Single().State);
        Assert.Null(rig.Store.Get(other.Id));
        Assert.False(Directory.Exists(rig.Store.DocumentFolder(other.Id)));
    }

    [Fact]
    public async Task ResumePending_finishes_what_an_earlier_run_left_half_done()
    {
        using var root = new TempRoot();
        var first = new DocumentStore(root.Path);
        DocumentRecord doc = first.Create();
        // Three pages as a killed app would have left them: nothing derived, only a thumbnail, finished but no outline.
        var pages = new[]
        {
            new PageRecord { Id = "pending", State = PageState.Pending },
            new PageRecord { Id = "preview", State = PageState.Preview, ExifOrientation = 6 },
            new PageRecord { Id = "ready", State = PageState.Ready },
        };
        foreach (PageRecord p in pages)
        {
            Directory.CreateDirectory(first.PageFolder(doc.Id, p.Id));
            File.WriteAllText(first.OriginalPath(doc.Id, p), p.Id);
            if (p.State != PageState.Pending) File.WriteAllText(first.ThumbPath(doc.Id, p), "thumb");
            if (p.State == PageState.Ready) File.WriteAllText(first.ProxyPath(doc.Id, p), "proxy");
        }
        first.Update(doc.Id, d => d.Pages.AddRange(pages));

        // Next app start: a fresh store and queue over the same folder.
        var store = new DocumentStore(root.Path);
        var images = new FakeImageService();
        var detection = new CropDetectionService(store, images, new FakeEdgeDetector(() => new QuadDetection(SomeQuad, 0.7, true)));
        var queue = new PageIngestQueue(store, images, detection);
        queue.ResumePending();
        await queue.WaitIdleAsync();

        Assert.All(store.Pages(doc.Id), p =>
        {
            Assert.Equal(PageState.Ready, p.State);
            Assert.NotNull(p.CropQuad);
        });
    }

    [Fact]
    public async Task Automatic_outlines_by_older_detection_rules_are_detected_again_once_and_hand_made_ones_are_kept()
    {
        using var root = new TempRoot();
        var first = new DocumentStore(root.Path);
        DocumentRecord doc = first.Create();
        double[] old = [0.1, 0.1, 0.9, 0.1, 0.9, 0.9, 0.1, 0.9];
        var pages = new[]
        {
            new PageRecord { Id = "auto", State = PageState.Ready, CropQuad = old, CropDetected = true },
            new PageRecord { Id = "missed", State = PageState.Ready, CropQuad = old, CropDetected = false },
            new PageRecord { Id = "hand", State = PageState.Ready, CropQuad = old, CropManual = true },
        };
        foreach (PageRecord p in pages)
        {
            Directory.CreateDirectory(first.PageFolder(doc.Id, p.Id));
            File.WriteAllText(first.OriginalPath(doc.Id, p), p.Id);
            File.WriteAllText(first.ProxyPath(doc.Id, p), "proxy");
        }
        first.Update(doc.Id, d => d.Pages.AddRange(pages));

        var store = new DocumentStore(root.Path);
        var images = new FakeImageService();
        int detections = 0;
        var detection = new CropDetectionService(store, images, new FakeEdgeDetector(() =>
        {
            Interlocked.Increment(ref detections);
            return new QuadDetection(SomeQuad, 0.7, true);
        }));
        var queue = new PageIngestQueue(store, images, detection);
        queue.ResumePending();
        await queue.WaitIdleAsync();

        PageRecord Page(string id) => store.Pages(doc.Id).Single(p => p.Id == id);
        Assert.Equal(SomeQuad.ToValues(), Page("auto").CropQuad);
        Assert.Equal(PageRecord.DetectionVersion, Page("auto").CropDetection);
        Assert.True(Page("missed").CropDetected);
        Assert.Equal(old, Page("hand").CropQuad);
        Assert.Equal(2, detections);

        // Once: the next start finds nothing to detect.
        var again = new PageIngestQueue(store, images, detection);
        again.ResumePending();
        await again.WaitIdleAsync();
        Assert.Equal(2, detections);
    }

    [Fact]
    public async Task DeletePage_and_Delete_remove_files()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        await rig.Import.ImportAsync(doc, [Src("1.jpg"), Src("2.jpg")]);
        await rig.Queue.WaitIdleAsync();

        PageRecord first = rig.Store.Pages(doc.Id)[0];
        string firstFolder = rig.Store.PageFolder(doc.Id, first.Id);
        rig.Store.DeletePage(doc.Id, first.Id);
        Assert.False(Directory.Exists(firstFolder));
        Assert.Single(new DocumentStore(rig.Root.Path).Get(doc.Id)!.Pages);

        rig.Store.Delete(doc.Id);
        Assert.Null(rig.Store.Get(doc.Id));
        Assert.False(Directory.Exists(rig.Store.DocumentFolder(doc.Id)));
    }

    [Fact]
    public async Task Detection_can_be_rerun_and_replaces_the_outline()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        await rig.Import.ImportAsync(doc, [Src("a.jpg")]);
        await rig.Queue.WaitIdleAsync();
        PageRecord page = rig.Store.Pages(doc.Id).Single();
        Assert.Null(page.CropQuad);

        var detection = new CropDetectionService(rig.Store, rig.Images, new FakeEdgeDetector(() => new QuadDetection(Quad.Inset(0.03), 0, false)));
        QuadDetection? d = await detection.DetectAsync(doc.Id, page.Id);

        Assert.False(d!.Detected);
        PageRecord back = new DocumentStore(rig.Root.Path).Get(doc.Id)!.Pages[0];
        Assert.Equal(Quad.Inset(0.03), Quad.FromValues(back.CropQuad!));
        Assert.False(back.CropDetected);
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests want the callback inline.</summary>
    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
