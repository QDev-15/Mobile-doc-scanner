using DocScanner.Core;
using ImageCoreService;

namespace DocScanner.Core.Tests;

/// <summary>Background rendering (<see cref="PageIngestQueue.Prerender"/>): pages are straightened once they have an
/// outline, a render the user asks for overtakes the waiting ones, and a page is never rendered twice at once.</summary>
public class PrerenderTests
{
    /// <summary>Region decodes wait at a gate and are logged (page folder + how many run at once per page).</summary>
    private sealed class GatedImages(FakeImageService inner) : IImageService
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, int> _running = [];
        public TaskCompletionSource Gate { get; set; } = Done();
        public List<string> Order { get; } = [];
        public int MaxConcurrentPerPage { get; private set; }
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource Done()
        {
            var t = new TaskCompletionSource();
            t.SetResult();
            return t;
        }

        public async Task<RgbImage> LoadRegionAsync(string o, int x, int y, int w, int h, int s, CancellationToken ct)
        {
            string page = Path.GetFileName(Path.GetDirectoryName(o)!);
            lock (_lock)
            {
                Order.Add(page);
                _running[page] = _running.GetValueOrDefault(page) + 1;
                MaxConcurrentPerPage = Math.Max(MaxConcurrentPerPage, _running[page]);
                if (!FirstEntered.TrySetResult()) SecondEntered.TrySetResult();
            }
            try
            {
                await Gate.Task;
                return await inner.LoadRegionAsync(o, x, y, w, h, s, ct);
            }
            finally
            {
                lock (_lock) _running[page]--;
            }
        }

        public Task<ImageInfo> CreateThumbAsync(string o, string t, int e, int r, CancellationToken ct) => inner.CreateThumbAsync(o, t, e, r, ct);
        public Task CreateProxyAsync(string o, string p, int e, int or, CancellationToken ct) => inner.CreateProxyAsync(o, p, e, or, ct);
        public Task<RgbImage> LoadRgbAsync(string p, int m, CancellationToken ct) => inner.LoadRgbAsync(p, m, ct);
        public Task SaveJpegAsync(RgbImage i, string p, int q, CancellationToken ct) => inner.SaveJpegAsync(i, p, q, ct);
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly DocumentStore Store;
        public readonly GatedImages Images = new(new FakeImageService());
        public readonly PageIngestQueue Queue;
        private readonly ImportService _import;

        public Rig(bool prerender = true)
        {
            Store = new DocumentStore(Root.Path);
            var detection = new CropDetectionService(Store, Images, new FakeEdgeDetector(() =>
                new QuadDetection(new Quad(new PointD(0.1, 0.1), new PointD(0.9, 0.1), new PointD(0.9, 0.9), new PointD(0.1, 0.9)), 0.8, true)));
            Queue = new PageIngestQueue(Store, Images, detection, 2, new CropRenderService(Store, Images)) { Prerender = prerender };
            _import = new ImportService(Store, Queue);
        }

        public async Task<DocumentRecord> Import(int pages)
        {
            DocumentRecord doc = Store.Create();
            await _import.ImportAsync(doc, Enumerable.Range(0, pages)
                .Select(i => new ImportSource($"{i}.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))).ToList());
            return doc;
        }

        public void Dispose() => Root.Dispose();
    }

    private static async Task Within(Task t) => Assert.Same(t, await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(20))));

    [Fact]
    public async Task Pages_are_rendered_in_the_background_once_they_have_their_outline()
    {
        using var rig = new Rig();
        DocumentRecord doc = await rig.Import(3);
        await Within(rig.Queue.WaitIdleAsync());

        foreach (PageRecord p in rig.Store.Pages(doc.Id))
        {
            Assert.NotNull(p.CropQuad);
            Assert.Equal(1, p.CroppedRevision);
            Assert.False(p.NeedsRender);
            Assert.True(File.Exists(rig.Store.CroppedPath(doc.Id, p)));
        }
    }

    [Fact]
    public async Task Without_the_option_the_queue_stops_after_detection()
    {
        using var rig = new Rig(prerender: false);
        DocumentRecord doc = await rig.Import(2);
        await Within(rig.Queue.WaitIdleAsync());
        Assert.All(rig.Store.Pages(doc.Id), p => Assert.Equal(0, p.CroppedRevision));
        Assert.Empty(rig.Images.Order);
    }

    [Fact]
    public async Task A_render_the_user_asks_for_overtakes_the_waiting_background_renders()
    {
        using var rig = new Rig();
        rig.Images.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DocumentRecord doc = await rig.Import(4);
        await Within(rig.Images.FirstEntered.Task); // one background render is running (held at the gate), the rest wait
        for (int i = 0; i < 200 && rig.Store.Pages(doc.Id).Any(p => p.CropQuad == null || rig.Queue.IsPreparing(p.Id)); i++)
            await Task.Delay(25); // the last outlines may still be coming on the other worker
        IReadOnlyList<PageRecord> pages = rig.Store.Pages(doc.Id);
        string first = rig.Images.Order[0];
        PageRecord last = pages[^1];
        Assert.NotEqual(first, last.Id);
        Assert.False(rig.Queue.IsPreparing(last.Id)); // only renders left for it: the result screen may ask

        rig.Queue.EnqueueRender(doc.Id, last.Id);
        await Within(rig.Images.SecondEntered.Task); // the free worker took it at once, beside the running prerender
        Assert.Equal([first, last.Id], rig.Images.Order);

        rig.Images.Gate.SetResult();
        await Within(rig.Queue.WaitIdleAsync());
        Assert.Equal(4, rig.Images.Order.Count); // every page rendered exactly once
        Assert.All(rig.Store.Pages(doc.Id), p => Assert.False(p.NeedsRender));
    }

    [Fact]
    public async Task A_page_is_never_rendered_twice_at_once_and_a_render_made_stale_meanwhile_gives_way_to_the_current_one()
    {
        using var rig = new Rig();
        rig.Images.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DocumentRecord doc = await rig.Import(1);
        await Within(rig.Images.FirstEntered.Task);
        string pageId = rig.Store.Pages(doc.Id).Single().Id;

        // The user moves the outline and switches to black and white while the background render is running: the
        // outline it started from is out of date, so the page must be rendered again, after it.
        rig.Store.Update(doc.Id, d =>
        {
            d.Pages[0].CropQuad = Quad.Inset(0.2).ToValues();
            d.Pages[0].ColorMode = PageColorMode.BlackWhite;
        });
        rig.Queue.EnqueueRender(doc.Id, pageId);
        rig.Queue.EnqueueRender(doc.Id, pageId); // asked twice (e.g. export polling): still one render
        await Task.Delay(200);
        Assert.Single(rig.Images.Order); // waits for the running one instead of racing it

        rig.Images.Gate.SetResult();
        await Within(rig.Queue.WaitIdleAsync());
        PageRecord p = rig.Store.Pages(doc.Id).Single();
        Assert.Equal(1, rig.Images.MaxConcurrentPerPage);
        Assert.Equal(2, rig.Images.Order.Count);
        Assert.Equal(1, p.CroppedRevision); // the out-of-date background render stopped before saving: only the current one was saved
        Assert.Equal(PageColorMode.BlackWhite, p.CroppedColorMode);
        Assert.Equal(".png", p.CroppedExtension);
        Assert.False(p.NeedsRender);
    }

    [Fact]
    public async Task A_failed_background_render_is_not_recorded_so_the_user_render_retries_it()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        var images = new FailingRegionImages(new FakeImageService());
        var detection = new CropDetectionService(store, images, new FakeEdgeDetector(() =>
            new QuadDetection(Quad.Inset(0.1), 0.8, true)));
        var queue = new PageIngestQueue(store, images, detection, 2, new CropRenderService(store, images)) { Prerender = true };
        DocumentRecord doc = store.Create();
        await new ImportService(store, queue).ImportAsync(doc, [new ImportSource("a.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1])))]);
        await Within(queue.WaitIdleAsync());

        PageRecord p = store.Pages(doc.Id).Single();
        Assert.Equal(1, images.Attempts);
        Assert.Null(p.RenderError);
        Assert.True(p.NeedsRender);
        Assert.Equal(PageState.Ready, p.State);

        queue.EnqueueRender(doc.Id, p.Id);
        await Within(queue.WaitIdleAsync());
        Assert.Equal(2, images.Attempts);
        Assert.NotNull(store.Pages(doc.Id).Single().RenderError); // the user-driven render reports it
    }

    private sealed class FailingRegionImages(FakeImageService inner) : IImageService
    {
        public int Attempts;
        public Task<RgbImage> LoadRegionAsync(string o, int x, int y, int w, int h, int s, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidDataException("cannot decode region");
        }

        public Task<ImageInfo> CreateThumbAsync(string o, string t, int e, int r, CancellationToken ct) => inner.CreateThumbAsync(o, t, e, r, ct);
        public Task CreateProxyAsync(string o, string p, int e, int or, CancellationToken ct) => inner.CreateProxyAsync(o, p, e, or, ct);
        public Task<RgbImage> LoadRgbAsync(string p, int m, CancellationToken ct) => inner.LoadRgbAsync(p, m, ct);
        public Task SaveJpegAsync(RgbImage i, string p, int q, CancellationToken ct) => inner.SaveJpegAsync(i, p, q, ct);
    }
}
