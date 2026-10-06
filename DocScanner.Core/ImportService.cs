using ImageCoreService;

namespace DocScanner.Core;

/// <summary>One photo to import; the stream is opened lazily, one photo at a time.</summary>
public sealed record ImportSource(string Name, Func<CancellationToken, Task<Stream>> OpenAsync)
{
    /// <summary>Optional: looks the real file name up when the photo is imported (on the import's background thread),
    /// for sources whose name costs a query (an Android content URI). Falls back to <see cref="Name"/>.</summary>
    public Func<string?>? ResolveName { get; init; }
}

public sealed record ImportFailure(string Name, string Message);

public sealed record ImportProgress(int Done, int Total);

/// <param name="Added">Pages that were added to the document (kept even when cancelled).</param>
public sealed record ImportResult(int Added, IReadOnlyList<ImportFailure> Failures, bool Cancelled);

/// <summary>Per-page news from an import, for a screen that shows each placeholder filling in.</summary>
public interface IImportObserver
{
    /// <summary>Share (0..1) of the photo copied so far; only when the photo's size is known.</summary>
    void CopyProgress(string pageId, double fraction);

    /// <summary>The placeholder became a real page (<see cref="PageState.Pending"/>) or failed.</summary>
    void PageChanged(string pageId);
}

/// <summary>
/// Adds picked / captured photos to a document in two steps:
///  1. <see cref="AddPlaceholders"/>: one <see cref="PageState.Importing"/> page per photo, in the picked order, at once
///     (a single save), so the document shows every photo's place before anything is copied;
///  2. <see cref="FillAsync"/>: each original is copied into its page folder and the page turns
///     <see cref="PageState.Pending"/> (saved), then the heavy work (thumbnail, proxy, outline) is left to
///     <see cref="PageIngestQueue"/>. A photo that cannot be read turns its page <see cref="PageState.Failed"/> in place;
///     a cancel removes the placeholders not filled yet. Pages already filled are kept whatever happens.
/// </summary>
public sealed class ImportService(DocumentStore store, PageIngestQueue queue)
{
    private static readonly HashSet<string> KnownExtensions = [".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".bmp", ".gif"];

    /// <summary>Report the copy of a photo in steps of this share (a 5 MB photo: every 200 KB).</summary>
    private const double ProgressStep = 0.04;

    public Task<ImportResult> ImportAsync(DocumentRecord doc, IReadOnlyList<ImportSource> sources,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default) =>
        ImportAsync(doc.Id, sources, progress, ct);

    /// <summary>Both steps at once.</summary>
    public Task<ImportResult> ImportAsync(string docId, IReadOnlyList<ImportSource> sources,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default) =>
        FillAsync(docId, AddPlaceholders(docId, sources.Count), sources, progress, ct);

    /// <summary>Appends <paramref name="count"/> placeholder pages; returns their ids (empty when the document is gone).</summary>
    public IReadOnlyList<string> AddPlaceholders(string docId, int count)
    {
        // Black-and-white by default (owner's request 2026-09-29): most imported pages are documents, not photos, and
        // black-and-white is both the crisper look and the much smaller file. One tap on the filter cards switches a
        // page that does need color. PageRecord.ColorMode itself still defaults to Color -- that default is for a
        // doc.json saved before the field existed, not for a genuinely new page (see the field's own doc comment).
        var pages = Enumerable.Range(0, count)
            .Select(_ => new PageRecord { Id = Guid.NewGuid().ToString("N"), State = PageState.Importing, ColorMode = PageColorMode.BlackWhite })
            .ToList();
        return count > 0 && store.Update(docId, d => d.Pages.AddRange(pages)) ? pages.Select(p => p.Id).ToList() : [];
    }

    /// <summary>Copies <paramref name="sources"/>[i] into the placeholder <paramref name="pageIds"/>[i]. A placeholder the
    /// user deleted meanwhile is skipped.</summary>
    public async Task<ImportResult> FillAsync(string docId, IReadOnlyList<string> pageIds, IReadOnlyList<ImportSource> sources,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default, IImportObserver? observer = null)
    {
        int added = 0, count = Math.Min(pageIds.Count, sources.Count);
        var failures = new List<ImportFailure>();
        progress?.Report(new ImportProgress(0, count));

        for (int i = 0; i < count; i++)
        {
            string pageId = pageIds[i];
            if (store.Get(docId) == null) break; // the document was deleted meanwhile
            if (ct.IsCancellationRequested)
            {
                RemovePlaceholders(docId, pageIds.Skip(i));
                return new ImportResult(added, failures, Cancelled: true);
            }
            if (!IsPlaceholder(docId, pageId))
            {
                progress?.Report(new ImportProgress(i + 1, count)); // deleted by the user before its turn
                continue;
            }

            ImportSource source = sources[i];
            string name = source.Name;
            string folder = store.PageFolder(docId, pageId);
            try
            {
                name = source.ResolveName?.Invoke() ?? source.Name;
                string extension = ExtensionOf(name);
                Directory.CreateDirectory(folder);
                string target = store.OriginalPath(docId, new PageRecord { Id = pageId, OriginalExtension = extension });
                using (Perf.Measure("import copy")) await CopyAsync(source, target, pageId, observer, ct);

                bool filled = false;
                store.Update(docId, d =>
                {
                    PageRecord? p = d.Pages.FirstOrDefault(x => x.Id == pageId);
                    if (p is not { State: PageState.Importing }) return;
                    p.OriginalExtension = extension;
                    p.State = PageState.Pending;
                    filled = true;
                });
                if (filled)
                {
                    queue.Enqueue(docId, pageId);
                    added++;
                }
                else
                {
                    DeleteQuietly(folder); // the placeholder was deleted while its photo was being copied
                }
            }
            catch (OperationCanceledException)
            {
                DeleteQuietly(folder);
                RemovePlaceholders(docId, pageIds.Skip(i));
                return new ImportResult(added, failures, Cancelled: true);
            }
            catch (Exception ex)
            {
                // One unreadable photo must not sink the rest of the batch: its page shows the error where it was.
                DeleteQuietly(folder);
                failures.Add(new ImportFailure(name, ex.Message));
                store.Update(docId, d =>
                {
                    PageRecord? p = d.Pages.FirstOrDefault(x => x.Id == pageId);
                    if (p is not { State: PageState.Importing }) return;
                    p.State = PageState.Failed;
                    p.Error = ex.Message;
                });
            }
            observer?.PageChanged(pageId);
            progress?.Report(new ImportProgress(i + 1, count));
        }
        return new ImportResult(added, failures, Cancelled: false);
    }

    private bool IsPlaceholder(string docId, string pageId) =>
        store.Pages(docId).Any(p => p.Id == pageId && p.State == PageState.Importing);

    /// <summary>Takes the placeholders that will not be filled out of the document (one save).</summary>
    private void RemovePlaceholders(string docId, IEnumerable<string> pageIds)
    {
        var ids = new HashSet<string>(pageIds);
        store.Update(docId, d => d.Pages.RemoveAll(p => ids.Contains(p.Id) && p.State == PageState.Importing));
        foreach (string id in ids) DeleteQuietly(store.PageFolder(docId, id));
    }

    private static async Task CopyAsync(ImportSource source, string target, string pageId, IImportObserver? observer, CancellationToken ct)
    {
        await using Stream input = await source.OpenAsync(ct);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1, useAsync: false);
        long length = input.CanSeek ? input.Length : -1;
        var buffer = new byte[256 * 1024];
        long copied = 0;
        double reported = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            output.Write(buffer, 0, n);
            copied += n;
            if (length > 0 && observer != null)
            {
                double f = Math.Min(1, (double)copied / length);
                if (f - reported >= ProgressStep || f >= 1)
                {
                    reported = f;
                    observer.CopyProgress(pageId, f);
                }
            }
        }
    }

    private static string ExtensionOf(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return KnownExtensions.Contains(ext) ? ext : ".jpg";
    }

    private static void DeleteQuietly(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
    }
}
