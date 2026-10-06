using ImageCoreService;

namespace DocScanner.Core;

/// <summary>Something about a page changed (state, thumbnail, outline...); the UI re-reads it.</summary>
public sealed record PageUpdate(string DocId, string PageId);

/// <summary>
/// Background pipeline that turns freshly added pages (state <see cref="PageState.Pending"/>) into
/// finished ones, in three stages of falling urgency:
///  1. thumbnail (a heavily sub-sampled decode, tens of ms): every page of a batch gets its
///     thumbnail before anything else happens, so the screen fills in almost at once;
///  2. screen proxy (a bigger decode): the page becomes openable;
///  3. paper-outline detection (the slowest): pages get their outline last.
/// A stage is only started when no page is waiting for an earlier one. Two workers keep a
/// multi-core phone busy without holding many decoded bitmaps in RAM at once. Progress is saved
/// in doc.json after every step, so <see cref="ResumePending"/> can continue after the app was
/// killed mid-batch.
///
/// With <see cref="Prerender"/> on, a page that has its outline is then straightened and filtered
/// in the background too (the lowest priority, on one worker at most, so the other stays free for
/// what the user is waiting on): opening its result and exporting it are then immediate. A page is
/// never rendered by two workers at once; a render the user asks for overtakes a queued prerender.
/// </summary>
public sealed class PageIngestQueue
{
    public const int ThumbEdge = 512;
    public const int ProxyEdge = 1600;

    private enum Stage { Render, Thumb, Proxy, Detect, Prerender }

    /// <summary>Background renders running at once: one, so a worker is always left for user-driven work.</summary>
    private const int MaxPrerenders = 1;

    /// <summary>Threads one background job may use for its image loops: a quarter of the cores (2 on an 8-core phone,
    /// so two workers leave half the cores to the screen).</summary>
    private static readonly int BackgroundThreads = Math.Max(1, Environment.ProcessorCount / 4);

    private readonly record struct Job(string DocId, string PageId, Stage Stage);

    private readonly DocumentStore _store;
    private readonly IImageService _images;
    private readonly CropDetectionService? _detection;
    private readonly CropRenderService? _render;
    private readonly int _maxWorkers;

    private readonly object _lock = new();
    private readonly Queue<Job> _renders = new(), _thumbs = new(), _proxies = new(), _detects = new(), _prerenders = new();
    private readonly Dictionary<string, int> _jobsPerPage = [];
    private readonly Dictionary<string, int> _renderJobsPerPage = []; // the render / prerender share of _jobsPerPage
    private readonly HashSet<string> _rendering = []; // pages with a render or prerender running
    private int _prerendersRunning;
    private int _running;
    private int _outstanding;
    private TaskCompletionSource _idle = NewCompleted();

    public PageIngestQueue(DocumentStore store, IImageService images, CropDetectionService? detection = null, int workers = 2, CropRenderService? render = null)
    {
        _store = store;
        _images = images;
        _detection = detection;
        _render = render;
        _maxWorkers = Math.Max(1, workers);
    }

    /// <summary>Straighten and filter pages in the background once they have an outline (off by default: tests
    /// of the other stages expect the queue to stop after detection).</summary>
    public bool Prerender { get; init; }

    /// <summary>Raised on a worker thread after any change to a page.</summary>
    public event Action<PageUpdate>? PageUpdated;

    /// <summary>Queued plus running jobs.</summary>
    public int Outstanding
    {
        get { lock (_lock) return _outstanding; }
    }

    /// <summary>True while any job for the page is queued or running.</summary>
    public bool IsBusy(string pageId)
    {
        lock (_lock) return _jobsPerPage.ContainsKey(pageId);
    }

    /// <summary>True while the page still has import work (thumbnail, proxy, outline detection) queued or running;
    /// renders do not count. A render asked for now would be made before the outline is known.</summary>
    public bool IsPreparing(string pageId)
    {
        lock (_lock) return _jobsPerPage.GetValueOrDefault(pageId) > _renderJobsPerPage.GetValueOrDefault(pageId);
    }

    /// <summary>Completes when nothing is queued or running (mainly for tests).</summary>
    public Task WaitIdleAsync()
    {
        lock (_lock) return _idle.Task;
    }

    /// <summary>Straightens the page from the original photo. The user is waiting for it (they just
    /// pressed Done), so it goes ahead of everything else in the queue: a queued prerender of the page
    /// is promoted, and a render already waiting is not queued twice. Safe to call repeatedly.</summary>
    public void EnqueueRender(string docId, string pageId)
    {
        lock (_lock)
        {
            if (_renders.Any(j => j.PageId == pageId)) return;
            if (Remove(_prerenders, pageId)) Forget(new Job(docId, pageId, Stage.Prerender));
            Add(new Job(docId, pageId, Stage.Render));
        }
    }

    /// <summary>Queues a background render of the page (when <see cref="Prerender"/> is on), e.g. after the user
    /// moved on from editing its outline. Does nothing when a render of the page is already waiting.</summary>
    public void EnqueuePrerender(string docId, string pageId)
    {
        if (!Prerender || _render == null) return;
        lock (_lock)
        {
            if (_renders.Any(j => j.PageId == pageId) || _prerenders.Any(j => j.PageId == pageId)) return;
            Add(new Job(docId, pageId, Stage.Prerender));
        }
    }

    /// <summary>Removes the page's job from a queue (under the lock); true if there was one.</summary>
    private static bool Remove(Queue<Job> queue, string pageId)
    {
        if (!queue.Any(j => j.PageId == pageId)) return false;
        Job[] keep = queue.Where(j => j.PageId != pageId).ToArray();
        queue.Clear();
        foreach (Job j in keep) queue.Enqueue(j);
        return true;
    }

    /// <summary>Bookkeeping for a job that finished, or was taken out of its queue without running (under the lock).</summary>
    private void Forget(Job job)
    {
        string pageId = job.PageId;
        if (--_jobsPerPage[pageId] == 0) _jobsPerPage.Remove(pageId);
        if (IsRender(job.Stage) && --_renderJobsPerPage[pageId] == 0) _renderJobsPerPage.Remove(pageId);
        if (--_outstanding == 0) _idle.TrySetResult();
    }

    /// <summary>Starts the pipeline for a page that was just added.
    public void Enqueue(string docId, string pageId) => Add(new Job(docId, pageId, Stage.Thumb));

    /// <summary>Picks up pages left unfinished by an earlier run (app killed, crash): pending ones start
    /// over, half-done ones continue, and finished pages that never got an outline (or have an automatic one by older
    /// detection rules, <see cref="PageRecord.DetectionVersion"/>) get one.</summary>
    public void ResumePending()
    {
        foreach (DocumentRecord doc in _store.List())
        {
            foreach (PageRecord page in _store.Pages(doc.Id))
            {
                if (IsBusy(page.Id)) continue;
                switch (page.State)
                {
                    case PageState.Pending: Add(new Job(doc.Id, page.Id, Stage.Thumb)); break;
                    case PageState.Preview: Add(new Job(doc.Id, page.Id, Stage.Proxy)); break;
                    case PageState.Ready when page.NeedsDetection && _detection != null:
                        Add(new Job(doc.Id, page.Id, Stage.Detect));
                        break;
                    case PageState.Ready when page.CropQuad != null && page.NeedsRender && page.RenderError == null:
                        EnqueuePrerender(doc.Id, page.Id);
                        break;
                }
            }
        }
    }

    private void Add(Job job)
    {
        lock (_lock)
        {
            if (_outstanding == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _outstanding++;
            _jobsPerPage[job.PageId] = _jobsPerPage.GetValueOrDefault(job.PageId) + 1;
            if (IsRender(job.Stage)) _renderJobsPerPage[job.PageId] = _renderJobsPerPage.GetValueOrDefault(job.PageId) + 1;
            (job.Stage switch
            {
                Stage.Render => _renders, Stage.Thumb => _thumbs, Stage.Proxy => _proxies, Stage.Detect => _detects, _ => _prerenders,
            }).Enqueue(job);

            if (_running < _maxWorkers)
            {
                _running++;
                _ = Task.Run(WorkerAsync);
            }
        }
    }

    private static bool IsRender(Stage stage) => stage is Stage.Render or Stage.Prerender;

    /// <summary>Next job to run (under the lock). Strict priority: nothing starts a later stage while an earlier one
    /// has runnable work waiting. A render of a page that is being rendered right now waits for that one to finish
    /// (two renders of a page would both write revision n + 1); the worker running it picks the job up afterwards.</summary>
    private bool TryDequeue(out Job job)
    {
        foreach (Queue<Job> q in new[] { _renders, _thumbs, _proxies, _detects, _prerenders })
        {
            if (q == _prerenders && _prerendersRunning >= MaxPrerenders) break;
            for (int n = q.Count; n > 0; n--)
            {
                Job next = q.Dequeue();
                if (IsRender(next.Stage) && _rendering.Contains(next.PageId))
                {
                    q.Enqueue(next);
                    continue;
                }
                if (IsRender(next.Stage)) _rendering.Add(next.PageId);
                if (next.Stage == Stage.Prerender) _prerendersRunning++;
                job = next;
                return true;
            }
        }
        job = default;
        return false;
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            Job job;
            lock (_lock)
            {
                if (!TryDequeue(out job))
                {
                    _running--;
                    return;
                }
            }

            try
            {
                // Background work keeps to a share of the cores: the page the user is editing gets the rest and stays smooth.
                using (ParallelScope.Limit(BackgroundThreads))
                using (Perf.Measure($"stage {job.Stage}"))
                    await RunAsync(job);
            }
            catch (Exception ex)
            {
                // RunAsync handles the expected failures itself; this only keeps the worker alive.
                Fail(job, ex);
            }
            finally
            {
                lock (_lock)
                {
                    if (IsRender(job.Stage)) _rendering.Remove(job.PageId);
                    if (job.Stage == Stage.Prerender) _prerendersRunning--;
                    Forget(job);
                }
            }
        }
    }

    private async Task RunAsync(Job job)
    {
        PageRecord? page = _store.Pages(job.DocId).FirstOrDefault(p => p.Id == job.PageId);
        if (page == null) return; // deleted while queued

        switch (job.Stage)
        {
            case Stage.Thumb when page.State == PageState.Pending:
            {
                ImageInfo info = await _images.CreateThumbAsync(
                    _store.OriginalPath(job.DocId, page), _store.ThumbPath(job.DocId, page), ThumbEdge, page.UserRotation, CancellationToken.None);
                int orientation = ImageGeometry.ComposeRotation(info.ExifOrientation, page.UserRotation);
                (int uw, int uh) = ImageGeometry.UprightSize(info.RawWidth, info.RawHeight, orientation);
                (int pw, int ph) = ImageGeometry.FitLongEdge(uw, uh, ProxyEdge);
                bool kept = Change(job, p =>
                {
                    p.RawWidth = info.RawWidth;
                    p.RawHeight = info.RawHeight;
                    p.ExifOrientation = info.ExifOrientation;
                    p.ProxyWidth = pw;
                    p.ProxyHeight = ph;
                    p.State = PageState.Preview;
                });
                if (kept) Add(new Job(job.DocId, job.PageId, Stage.Proxy));
                break;
            }

            case Stage.Proxy when page.State == PageState.Preview:
            {
                await _images.CreateProxyAsync(_store.OriginalPath(job.DocId, page), _store.ProxyPath(job.DocId, page),
                    ProxyEdge, page.EffectiveOrientation, CancellationToken.None);
                bool kept = Change(job, p => p.State = PageState.Ready);
                // A page that already has an outline (rotated by the user, or edited by hand) keeps it.
                if (kept && _detection != null && page.NeedsDetection) Add(new Job(job.DocId, job.PageId, Stage.Detect));
                else if (kept && page.CropQuad != null) EnqueuePrerender(job.DocId, job.PageId);
                break;
            }

            case Stage.Render when _render != null && page.State == PageState.Ready && page.NeedsRender:
                try
                {
                    await _render.RenderAsync(job.DocId, job.PageId);
                }
                catch (OperationCanceledException)
                {
                    break; // the page changed while it was being rendered: that render was out of date, not failed
                }
                catch (Exception ex)
                {
                    Change(job, p => p.RenderError = ex.Message);
                    break;
                }
                Raise(job);
                break;

            case Stage.Detect when _detection != null && page.State == PageState.Ready && page.NeedsDetection:
                try
                {
                    await _detection.DetectAsync(job.DocId, job.PageId);
                }
                catch (Exception)
                {
                    // The outline is a convenience: the page works without it and gets it again when opened.
                }
                Raise(job);
                EnqueuePrerender(job.DocId, job.PageId);
                break;

            case Stage.Prerender when _render != null && page.State == PageState.Ready && page.CropQuad != null
                                      && page.NeedsRender && page.RenderError == null:
                try
                {
                    await _render.RenderAsync(job.DocId, job.PageId);
                }
                catch (Exception)
                {
                    // Only a head start: the render the user asks for later retries and reports the error.
                    break;
                }
                Raise(job);
                break;
        }
    }

    /// <summary>Applies a change to the page under the store lock; false when the page is gone.</summary>
    private bool Change(Job job, Action<PageRecord> change)
    {
        bool found = false;
        bool docExists = _store.Update(job.DocId, d =>
        {
            PageRecord? p = d.Pages.FirstOrDefault(x => x.Id == job.PageId);
            if (p == null) return;
            found = true;
            change(p);
        });
        if (docExists && found) Raise(job);
        return docExists && found;
    }

    private void Fail(Job job, Exception ex) =>
        Change(job, p =>
        {
            // Only the decode stages can fail a page; a failed detection just leaves it without an outline.
            if (job.Stage is Stage.Detect or Stage.Prerender) return;
            if (job.Stage == Stage.Render) { p.RenderError = ex.Message; return; }
            p.State = PageState.Failed;
            p.Error = ex.Message;
        });

    private void Raise(Job job) => PageUpdated?.Invoke(new PageUpdate(job.DocId, job.PageId));

    private static TaskCompletionSource NewCompleted()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}
