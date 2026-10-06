using DocScanner.Core;

namespace DocScanner.Core.Tests;

/// <summary><see cref="BackgroundImporter"/>: picking returns at once, photos are copied in the background into pages
/// one by one, and the document's status is there for whoever looks (the document screen, the home list).</summary>
public class BackgroundImportTests
{
    private sealed class Rig : IDisposable
    {
        public readonly TempRoot Root = new();
        public readonly DocumentStore Store;
        public readonly PageIngestQueue Queue;
        public readonly BackgroundImporter Importer;

        public Rig()
        {
            Store = new DocumentStore(Root.Path);
            Queue = new PageIngestQueue(Store, new FakeImageService());
            Importer = new BackgroundImporter(new ImportService(Store, Queue));
        }

        public void Dispose() => Root.Dispose();
    }

    /// <summary>Photos whose streams only open once the test lets them (one gate per photo).</summary>
    private static (List<ImportSource> Sources, TaskCompletionSource[] Gates) Gated(int n, string prefix = "p")
    {
        var gates = Enumerable.Range(0, n).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var sources = Enumerable.Range(0, n).Select(i => new ImportSource($"{prefix}{i}.jpg", async ct =>
        {
            await gates[i].Task.WaitAsync(ct);
            return (Stream)new MemoryStream(System.Text.Encoding.ASCII.GetBytes($"{prefix}{i}"));
        })).ToList();
        return (sources, gates);
    }

    private static ImportSource Ok(string name) => new(name, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));

    /// <summary>Pages whose photo is in (not placeholders).</summary>
    private static int Filled(Rig rig, string docId) => rig.Store.Pages(docId).Count(p => p.State != PageState.Importing);

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task Start_returns_at_once_and_pages_appear_one_by_one_with_a_status_until_the_import_is_done()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        (List<ImportSource> sources, TaskCompletionSource[] gates) = Gated(100);
        var changes = 0;
        rig.Importer.Changed += id => { if (id == doc.Id) Interlocked.Increment(ref changes); };

        rig.Importer.Start(doc.Id, sources); // nothing is copied yet: Start must not wait for it
        // Every photo has its place at once (the screen shows 100 "loading" tiles), saved in the document.
        IReadOnlyList<PageRecord> placeholders = rig.Store.Pages(doc.Id);
        Assert.Equal(100, placeholders.Count);
        Assert.All(placeholders, p => Assert.Equal(PageState.Importing, p.State));
        Assert.Equal(100, new DocumentStore(rig.Root.Path).Get(doc.Id)!.Pages.Count);
        ImportStatus? s = rig.Importer.Status(doc.Id);
        Assert.NotNull(s);
        Assert.True(s.Running);
        Assert.Equal(100, s.Total);
        Assert.Equal(0, s.Done);
        Assert.True(rig.Importer.IsImporting(doc.Id));

        gates[0].SetResult();
        gates[1].SetResult();
        await Until(() => Filled(rig, doc.Id) == 2);
        await Until(() => rig.Importer.Status(doc.Id)!.Done == 2);
        IReadOnlyList<PageRecord> now = rig.Store.Pages(doc.Id);
        Assert.Equal(placeholders.Take(3).Select(p => p.Id), now.Take(3).Select(p => p.Id)); // filled in place, same pages
        Assert.Equal([PageState.Importing], now.Skip(2).Select(p => p.State).Distinct());

        foreach (TaskCompletionSource g in gates) g.TrySetResult();
        await rig.Importer.WaitIdleAsync();
        Assert.Equal(100, Filled(rig, doc.Id));
        Assert.Null(rig.Importer.Status(doc.Id)); // all copied, nothing to report: the status goes away by itself
        Assert.False(rig.Importer.IsImporting(doc.Id));
        Assert.True(changes >= 100);
        await rig.Queue.WaitIdleAsync();
    }

    [Fact]
    public async Task Pages_keep_the_picked_order_and_a_second_pick_is_added_after_the_first()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        (List<ImportSource> first, TaskCompletionSource[] gates) = Gated(3, "a");
        rig.Importer.Start(doc.Id, first);
        rig.Importer.Start(doc.Id, [Ok("b0.jpg"), Ok("b1.jpg")]);
        Assert.Equal(5, rig.Importer.Status(doc.Id)!.Total);

        foreach (TaskCompletionSource g in gates) g.SetResult();
        await rig.Importer.WaitIdleAsync();
        IReadOnlyList<PageRecord> pages = rig.Store.Pages(doc.Id);
        Assert.Equal(5, pages.Count);
        string[] content = pages.Take(3).Select(p => File.ReadAllText(rig.Store.OriginalPath(doc.Id, p))).ToArray();
        Assert.Equal(["a0", "a1", "a2"], content);
        await rig.Queue.WaitIdleAsync();
    }

    [Fact]
    public async Task Stopping_keeps_the_pages_already_added_drops_the_rest_and_reports_until_dismissed()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        (List<ImportSource> sources, TaskCompletionSource[] gates) = Gated(10);
        rig.Importer.Start(doc.Id, sources);
        rig.Importer.Start(doc.Id, [Ok("later.jpg")]); // a second batch still waiting is dropped too
        gates[0].SetResult();
        gates[1].SetResult();
        await Until(() => Filled(rig, doc.Id) == 2);

        rig.Importer.Stop(doc.Id); // photo 3 is waiting to open: abandoned
        await rig.Importer.WaitIdleAsync();

        ImportStatus s = rig.Importer.Status(doc.Id)!;
        Assert.False(s.Running);
        Assert.True(s.Stopped);
        Assert.Equal(11, s.Total);
        Assert.Equal(2, s.Done);
        Assert.Equal(2, rig.Store.Pages(doc.Id).Count);
        Assert.Equal(2, Directory.GetDirectories(rig.Store.DocumentFolder(doc.Id)).Length); // no half-copied page left behind

        rig.Importer.Dismiss(doc.Id);
        Assert.Null(rig.Importer.Status(doc.Id));
        await rig.Queue.WaitIdleAsync();
    }

    [Fact]
    public async Task Unreadable_photos_are_reported_and_the_others_imported()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        rig.Importer.Start(doc.Id,
        [
            Ok("1.jpg"),
            new ImportSource("broken.jpg", _ => throw new IOException("gone")),
            Ok("3.jpg"),
        ]);
        await rig.Importer.WaitIdleAsync();

        IReadOnlyList<PageRecord> all = rig.Store.Pages(doc.Id);
        Assert.Equal(3, all.Count); // the unreadable photo keeps its place, as a failed page
        Assert.Equal(PageState.Failed, all[1].State);
        Assert.Equal("gone", all[1].Error);
        ImportStatus s = rig.Importer.Status(doc.Id)!;
        Assert.False(s.Running);
        Assert.False(s.Stopped);
        Assert.Equal(3, s.Done);
        Assert.Equal("broken.jpg", Assert.Single(s.Failures).Name);

        // A new pick replaces the old report.
        rig.Importer.Start(doc.Id, [Ok("4.jpg")]);
        await rig.Importer.WaitIdleAsync();
        Assert.Null(rig.Importer.Status(doc.Id));
        await rig.Queue.WaitIdleAsync();
    }

    [Fact]
    public async Task Deleting_the_document_mid_import_stops_quietly()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        DocumentRecord other = rig.Store.Create();
        (List<ImportSource> sources, TaskCompletionSource[] gates) = Gated(5);
        rig.Importer.Start(doc.Id, sources);
        rig.Importer.Start(other.Id, [Ok("x.jpg")]); // another document's import still runs afterwards
        gates[0].SetResult();
        await Until(() => Filled(rig, doc.Id) == 1);

        rig.Importer.Stop(doc.Id);
        rig.Store.Delete(doc.Id);
        foreach (TaskCompletionSource g in gates) g.TrySetResult();
        await rig.Importer.WaitIdleAsync();

        Assert.Null(rig.Store.Get(doc.Id));
        Assert.Single(rig.Store.Pages(other.Id));
        await rig.Queue.WaitIdleAsync();
    }

    [Fact]
    public async Task The_real_file_name_is_looked_up_during_the_import_for_the_extension()
    {
        using var rig = new Rig();
        DocumentRecord doc = rig.Store.Create();
        int lookups = 0;
        var source = new ImportSource("photo1.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1])))
        {
            ResolveName = () => { Interlocked.Increment(ref lookups); return "IMG_2031.PNG"; },
        };
        rig.Importer.Start(doc.Id, [source]);

        await rig.Importer.WaitIdleAsync();
        Assert.Equal(1, lookups);
        Assert.Equal(".png", rig.Store.Pages(doc.Id).Single().OriginalExtension);
        await rig.Queue.WaitIdleAsync();
    }
}
