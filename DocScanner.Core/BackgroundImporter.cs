namespace DocScanner.Core;

/// <summary>How the photos being added to one document are doing.</summary>
/// <param name="Total">Photos handed over for this document since its status appeared.</param>
/// <param name="Done">Photos handled so far (copied or failed; a stopped batch leaves the rest undone).</param>
/// <param name="Running">True while photos are still waiting or being copied.</param>
/// <param name="Stopped">The user stopped the import: the photos not yet copied were dropped.</param>
public sealed record ImportStatus(int Total, int Done, bool Running, bool Stopped, IReadOnlyList<ImportFailure> Failures)
{
    public int Remaining => Total - Done;
}

/// <summary>
/// Copies picked photos into documents in the background, so picking 100 photos never blocks the app: the caller
/// hands a batch over and returns at once. One worker copies the batches in the order they came (a copy is disk and
/// storage-provider bound, so parallel copies would only compete); every copied photo becomes a page at once
/// (<see cref="ImportService"/>) and goes on to <see cref="PageIngestQueue"/>.
///
/// The status of each document is kept here, not in a screen, so a user who leaves the document and comes back sees
/// the import still going. It lives as long as the process: photos not yet copied when the app is killed are not
/// imported (the picker's read permission ends with the app anyway). A status with nothing to report (all copied, no
/// failure) disappears on its own; one with failures or a stop stays until <see cref="Dismiss"/>.
/// </summary>
public sealed class BackgroundImporter(ImportService import)
{
    private sealed class Batch(string docId, IReadOnlyList<string> pageIds, IReadOnlyList<ImportSource> sources)
    {
        public string DocId { get; } = docId;
        public IReadOnlyList<string> PageIds { get; } = pageIds;
        public IReadOnlyList<ImportSource> Sources { get; } = sources;
        public CancellationTokenSource Cts { get; } = new();
        public int Done;
        public bool Dropped; // stopped before it started (set under the lock)
    }

    private sealed class DocState
    {
        public readonly List<Batch> Batches = []; // waiting and running
        public int Total, DoneInFinishedBatches;
        public bool Stopped;
        public readonly List<ImportFailure> Failures = [];
        public int Done => DoneInFinishedBatches + Batches.Sum(b => b.Done);
    }

    private readonly object _lock = new();
    private readonly Queue<Batch> _queue = new();
    private readonly Dictionary<string, DocState> _docs = [];
    private readonly Dictionary<string, double> _copying = []; // page id -> share copied, while its photo is copied
    private bool _working;
    private TaskCompletionSource _idle = Completed();

    /// <summary>A document's status changed, or pages were added / removed (argument: its id). Raised on a background
    /// thread, except the one from <see cref="Start"/> (on the caller's).</summary>
    public event Action<string>? Changed;

    /// <summary>One page changed: its photo is being copied (see <see cref="CopyProgress"/>), or it was filled / failed.
    /// Arguments: document id, page id. Raised on a background thread.</summary>
    public event Action<string, string>? PageChanged;

    /// <summary>Share (0..1) of the page's photo copied so far; null when it is not being copied.</summary>
    public double? CopyProgress(string pageId)
    {
        lock (_lock) return _copying.TryGetValue(pageId, out double f) ? f : null;
    }

    /// <summary>Adds one placeholder page per photo to the document at once (the screen shows them all right away), queues
    /// the copying, and returns.</summary>
    public void Start(string docId, IReadOnlyList<ImportSource> sources)
    {
        if (sources.Count == 0) return;
        IReadOnlyList<string> pageIds = import.AddPlaceholders(docId, sources.Count);
        if (pageIds.Count == 0) return; // the document is gone
        lock (_lock)
        {
            if (!_docs.TryGetValue(docId, out DocState? state))
                _docs[docId] = state = new DocState();
            else if (!state.Batches.Any())
            {
                // A finished status (failures / stop) is replaced by the new import.
                _docs[docId] = state = new DocState();
            }
            else
            {
                state.Stopped = false; // picked again while a stop was finishing: the new photos are wanted
            }
            var batch = new Batch(docId, pageIds, sources);
            state.Batches.Add(batch);
            state.Total += sources.Count;
            _queue.Enqueue(batch);
            if (!_working)
            {
                _working = true;
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(WorkAsync);
            }
        }
        Changed?.Invoke(docId);
    }

    /// <summary>The document's import status; null when there is nothing to show.</summary>
    public ImportStatus? Status(string docId)
    {
        lock (_lock)
        {
            if (!_docs.TryGetValue(docId, out DocState? s)) return null;
            return new ImportStatus(s.Total, s.Done, s.Batches.Count > 0, s.Stopped, s.Failures.ToList());
        }
    }

    public bool IsImporting(string docId)
    {
        lock (_lock) return _docs.TryGetValue(docId, out DocState? s) && s.Batches.Count > 0;
    }

    /// <summary>Stops the document's import: the photo being copied is abandoned, the ones not started are dropped.
    /// Pages already added stay.</summary>
    public void Stop(string docId)
    {
        Batch[] batches;
        lock (_lock)
        {
            if (!_docs.TryGetValue(docId, out DocState? s) || s.Batches.Count == 0) return;
            s.Stopped = true;
            batches = s.Batches.ToArray();
            foreach (Batch b in batches) b.Dropped = true;
        }
        // Outside the lock: Cancel runs the copy's continuation inline, which finishes the batch (and takes the lock).
        foreach (Batch b in batches)
        {
            try { b.Cts.Cancel(); }
            catch (ObjectDisposedException) { } // that batch finished meanwhile
        }
        Changed?.Invoke(docId);
    }

    /// <summary>Forgets a finished status (the user has seen the failures / the stop). A running import is kept.</summary>
    public void Dismiss(string docId)
    {
        lock (_lock)
        {
            if (!_docs.TryGetValue(docId, out DocState? s) || s.Batches.Count > 0) return;
            _docs.Remove(docId);
        }
        Changed?.Invoke(docId);
    }

    /// <summary>Completes when no batch is waiting or running (mainly for tests).</summary>
    public Task WaitIdleAsync()
    {
        lock (_lock) return _idle.Task;
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            Batch batch;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out batch!))
                {
                    _working = false;
                    _idle.TrySetResult();
                    return;
                }
            }

            ImportResult? result;
            bool dropped;
            lock (_lock) dropped = batch.Dropped;
            // Reported synchronously from the copy loop: a Progress<T> would post it and could land after the batch ended.
            var progress = new ForwardProgress(done =>
            {
                lock (_lock) batch.Done = done;
                Changed?.Invoke(batch.DocId);
            });
            try
            {
                // A dropped batch runs with a cancelled token: FillAsync then only takes its placeholders away.
                result = await import.FillAsync(batch.DocId, batch.PageIds, batch.Sources, progress,
                    dropped ? new CancellationToken(canceled: true) : batch.Cts.Token, new Observer(this, batch.DocId));
            }
            catch (Exception ex)
            {
                // FillAsync handles per-photo failures itself; this keeps the worker alive for the other batches.
                result = new ImportResult(0, [new ImportFailure("", ex.Message)], Cancelled: false);
            }

            lock (_lock)
            {
                DocState s = _docs[batch.DocId];
                s.Batches.Remove(batch);
                s.DoneInFinishedBatches += batch.Done;
                s.Failures.AddRange(result.Failures);
                foreach (string id in batch.PageIds) _copying.Remove(id);
                if (s.Batches.Count == 0 && s.Failures.Count == 0 && !s.Stopped) _docs.Remove(batch.DocId); // nothing to report
                batch.Cts.Dispose();
            }
            Changed?.Invoke(batch.DocId);
        }
    }

    private static TaskCompletionSource Completed()
    {
        var t = new TaskCompletionSource();
        t.SetResult();
        return t;
    }

    private sealed class ForwardProgress(Action<int> report) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value) => report(value.Done);
    }

    private sealed class Observer(BackgroundImporter owner, string docId) : IImportObserver
    {
        public void CopyProgress(string pageId, double fraction)
        {
            lock (owner._lock) owner._copying[pageId] = fraction;
            owner.PageChanged?.Invoke(docId, pageId);
        }

        public void PageChanged(string pageId)
        {
            lock (owner._lock) owner._copying.Remove(pageId);
            owner.PageChanged?.Invoke(docId, pageId);
        }
    }
}
