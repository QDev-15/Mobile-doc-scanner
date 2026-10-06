using System.Text.Json;

namespace DocScanner.Core;

/// <summary>
/// Documents on disk: <c>root/{docId}/doc.json</c> plus one folder per page
/// (<c>original.*</c>, <c>proxy.jpg</c>, <c>thumb.jpg</c>). doc.json is written atomically
/// (temp file + replace) so a crash or a killed app never leaves a half-written document.
///
/// Each document lives in memory exactly once: the UI, the import and the background pipeline
/// all get the same <see cref="DocumentRecord"/> instance, and every change goes through
/// <see cref="Update"/>, which serializes writers and saves. (Separate copies would overwrite
/// each other's progress.)
/// </summary>
public sealed class DocumentStore(string root)
{
    private const string DocFileName = "doc.json";
    private readonly object _gate = new();
    private readonly Dictionary<string, DocumentRecord> _cache = [];

    public string Root => root;

    public DocumentRecord Create(string? name = null)
    {
        var doc = new DocumentRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? $"Tài liệu {DateTime.Now:dd-MM-yyyy HH:mm}" : name.Trim(),
            CreatedUtc = DateTime.UtcNow,
        };
        lock (_gate)
        {
            _cache[doc.Id] = doc;
            SaveLocked(doc);
        }
        return doc;
    }

    /// <summary>All readable documents, newest first. A corrupt doc.json is skipped rather
    /// than hiding every other document.</summary>
    public IReadOnlyList<DocumentRecord> List()
    {
        lock (_gate)
        {
            if (!Directory.Exists(root)) return [];
            var result = new List<DocumentRecord>();
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                DocumentRecord? doc = GetLocked(Path.GetFileName(dir));
                if (doc != null) result.Add(doc);
            }
            return result.OrderByDescending(d => d.CreatedUtc).ToList();
        }
    }

    /// <summary>The shared in-memory document, read from disk on first use.</summary>
    public DocumentRecord? Get(string id)
    {
        CheckId(id);
        lock (_gate) return GetLocked(id);
    }

    /// <summary>Snapshot of a document's pages that is safe to enumerate while the background
    /// pipeline changes the document. (The page objects are shared; only their fields change.)</summary>
    public IReadOnlyList<PageRecord> Pages(string docId)
    {
        CheckId(docId);
        lock (_gate) return GetLocked(docId)?.Pages.ToList() ?? [];
    }

    /// <summary>Applies <paramref name="mutate"/> to the document under the store lock and saves it.
    /// Returns false when the document no longer exists (e.g. deleted while a job was queued).</summary>
    public bool Update(string docId, Action<DocumentRecord> mutate)
    {
        CheckId(docId);
        lock (_gate)
        {
            DocumentRecord? doc = GetLocked(docId);
            if (doc == null) return false;
            mutate(doc);
            SaveLocked(doc);
            return true;
        }
    }

    public void Delete(string id)
    {
        CheckId(id);
        lock (_gate)
        {
            _cache.Remove(id);
            string folder = DocumentFolder(id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    public void DeletePage(string docId, string pageId)
    {
        CheckId(docId);
        CheckId(pageId);
        lock (_gate)
        {
            DocumentRecord? doc = GetLocked(docId);
            if (doc == null) return;
            doc.Pages.RemoveAll(p => p.Id == pageId);
            SaveLocked(doc);
            string folder = PageFolder(docId, pageId);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    #region Folders

    private const string FoldersFileName = "folders.json";
    private List<FolderRecord>? _folders;

    /// <summary>All folders, by name (the main screen lists them before the documents).</summary>
    public IReadOnlyList<FolderRecord> Folders()
    {
        lock (_gate) return [.. FoldersLocked().OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public FolderRecord? Folder(string id)
    {
        lock (_gate) return FoldersLocked().FirstOrDefault(f => f.Id == id);
    }

    /// <summary><paramref name="parentFolderId"/> null creates a top-level folder; otherwise it must already
    /// exist (silently treated as null -- top level -- if not, rather than creating a folder no one can ever
    /// reach by navigating).</summary>
    public FolderRecord CreateFolder(string name, string? parentFolderId = null)
    {
        lock (_gate)
        {
            if (parentFolderId != null && FoldersLocked().All(f => f.Id != parentFolderId)) parentFolderId = null;
            var folder = new FolderRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? "Thư mục mới" : name.Trim(),
                ParentFolderId = parentFolderId,
                CreatedUtc = DateTime.UtcNow,
            };
            FoldersLocked().Add(folder);
            SaveFoldersLocked();
            return folder;
        }
    }

    /// <summary>Direct sub-folders of <paramref name="parentFolderId"/> (null = top level), by name.</summary>
    public IReadOnlyList<FolderRecord> ChildFolders(string? parentFolderId)
    {
        lock (_gate)
            return [.. FoldersLocked().Where(f => f.ParentFolderId == parentFolderId).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>This folder's own chain of ancestors, nearest first, as far up as it goes (empty for a
    /// top-level folder). Used for breadcrumbs and to keep <see cref="MoveFolder"/> from creating a cycle.</summary>
    public IReadOnlyList<FolderRecord> Ancestors(string folderId)
    {
        lock (_gate)
        {
            List<FolderRecord> all = FoldersLocked();
            var chain = new List<FolderRecord>();
            var seen = new HashSet<string> { folderId }; // guards against a cycle already on disk
            string? current = all.FirstOrDefault(f => f.Id == folderId)?.ParentFolderId;
            while (current != null && seen.Add(current))
            {
                FolderRecord? f = all.FirstOrDefault(x => x.Id == current);
                if (f == null) break;
                chain.Add(f);
                current = f.ParentFolderId;
            }
            return chain;
        }
    }

    /// <summary>Moves a folder under a different parent (null = top level). Refuses -- returns false, changes
    /// nothing -- a move into itself or into one of its own descendants, which would otherwise disconnect that
    /// whole branch from the root and make it unreachable.</summary>
    public bool MoveFolder(string folderId, string? newParentFolderId)
    {
        if (folderId == newParentFolderId) return false;
        lock (_gate)
        {
            List<FolderRecord> all = FoldersLocked();
            FolderRecord? folder = all.FirstOrDefault(f => f.Id == folderId);
            if (folder == null) return false;
            if (newParentFolderId != null)
            {
                if (all.All(f => f.Id != newParentFolderId)) return false;
                for (string? id = newParentFolderId; id != null; id = all.FirstOrDefault(f => f.Id == id)?.ParentFolderId)
                    if (id == folderId) return false; // newParentFolderId is folderId itself or one of its descendants
            }
            folder.ParentFolderId = newParentFolderId;
            SaveFoldersLocked();
            return true;
        }
    }

    public bool RenameFolder(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_gate)
        {
            FolderRecord? f = FoldersLocked().FirstOrDefault(x => x.Id == id);
            if (f == null) return false;
            f.Name = name.Trim();
            SaveFoldersLocked();
            return true;
        }
    }

    /// <summary>Removes a folder; nothing inside it is deleted. Documents and sub-folders that were directly in
    /// it move up to ITS OWN parent (top level for a top-level folder) -- so deleting one level of a nested tree
    /// only collapses that one level, it does not scatter everything all the way back to the top.</summary>
    public bool DeleteFolder(string id)
    {
        string? parent;
        lock (_gate)
        {
            FolderRecord? folder = FoldersLocked().FirstOrDefault(f => f.Id == id);
            if (folder == null) return false;
            parent = folder.ParentFolderId;
            FoldersLocked().RemoveAll(f => f.Id == id);
            foreach (FolderRecord child in FoldersLocked().Where(f => f.ParentFolderId == id)) child.ParentFolderId = parent;
            SaveFoldersLocked();
        }
        foreach (DocumentRecord d in List().Where(d => d.FolderId == id)) Update(d.Id, x => x.FolderId = parent);
        return true;
    }

    /// <summary>Files documents into a folder (null: the top level). Only the folder changes: their order, which is by
    /// creation time, stays as it was.</summary>
    public int MoveToFolder(IEnumerable<string> docIds, string? folderId)
    {
        if (folderId != null && Folder(folderId) == null) return 0;
        int moved = 0;
        foreach (string id in docIds.Distinct())
            if (Update(id, d => d.FolderId = folderId)) moved++;
        return moved;
    }

    private List<FolderRecord> FoldersLocked()
    {
        if (_folders != null) return _folders;
        string path = Path.Combine(root, FoldersFileName);
        try
        {
            _folders = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), DocumentJsonContext.Default.ListFolderRecord) ?? []
                : [];
        }
        catch (JsonException)
        {
            _folders = [];
        }
        return _folders;
    }

    private void SaveFoldersLocked()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, FoldersFileName), tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(FoldersLocked(), DocumentJsonContext.Default.ListFolderRecord));
        File.Move(tmp, path, overwrite: true);
    }

    #endregion

    /// <summary>Renames a document (blank names are ignored). Returns false when it no longer exists.</summary>
    public bool Rename(string docId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return Update(docId, d => d.Name = name.Trim());
    }

    /// <summary>Moves a page to <paramref name="newIndex"/> (clamped) in the document's order.</summary>
    public bool MovePage(string docId, string pageId, int newIndex)
    {
        bool moved = false;
        Update(docId, d =>
        {
            int from = d.Pages.FindIndex(p => p.Id == pageId);
            if (from < 0) return;
            int to = Math.Clamp(newIndex, 0, d.Pages.Count - 1);
            if (to == from) return;
            PageRecord page = d.Pages[from];
            d.Pages.RemoveAt(from);
            d.Pages.Insert(to, page);
            moved = true;
        });
        return moved;
    }

    /// <summary>The page <paramref name="delta"/> places after (+) or before (-) the given one, with its 0-based
    /// index and the page count; null at either end, or when the page is gone.</summary>
    public (string PageId, int Index, int Count)? Neighbor(string docId, string pageId, int delta)
    {
        IReadOnlyList<PageRecord> pages = Pages(docId);
        int i = pages.ToList().FindIndex(p => p.Id == pageId);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= pages.Count) return null;
        return (pages[j].Id, j, pages.Count);
    }

    /// <summary>Puts the pages in exactly this order (ids of the document's pages; unknown ids ignored,
    /// pages not listed keep their relative order at the end). Used to undo a move.</summary>
    public bool SetOrder(string docId, IReadOnlyList<string> pageIds) =>
        Update(docId, d =>
        {
            var byId = d.Pages.ToDictionary(p => p.Id);
            var ordered = pageIds.Where(byId.ContainsKey).Distinct().Select(id => byId[id]).ToList();
            ordered.AddRange(d.Pages.Where(p => !ordered.Contains(p)));
            d.Pages.Clear();
            d.Pages.AddRange(ordered);
        });

    /// <summary>Deletes a page but keeps it restorable: its folder moves to the document's trash and the
    /// record is returned (with its old position) for <see cref="RestorePage"/>. Null if not found.</summary>
    public DeletedPage? TrashPage(string docId, string pageId)
    {
        CheckId(docId);
        CheckId(pageId);
        lock (_gate)
        {
            DocumentRecord? doc = GetLocked(docId);
            int index = doc?.Pages.FindIndex(p => p.Id == pageId) ?? -1;
            if (doc == null || index < 0) return null;
            PageRecord page = doc.Pages[index];
            doc.Pages.RemoveAt(index);
            SaveLocked(doc);

            string folder = PageFolder(docId, pageId), trash = TrashFolder(docId, pageId);
            if (Directory.Exists(folder))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
                if (Directory.Exists(trash)) Directory.Delete(trash, recursive: true);
                Directory.Move(folder, trash);
            }
            return new DeletedPage(docId, page, index);
        }
    }

    /// <summary>Puts a trashed page back at its old position. False when its files are gone (trash emptied).</summary>
    public bool RestorePage(DeletedPage deleted)
    {
        lock (_gate)
        {
            DocumentRecord? doc = GetLocked(deleted.DocId);
            string trash = TrashFolder(deleted.DocId, deleted.Page.Id);
            if (doc == null || !Directory.Exists(trash) || doc.Pages.Any(p => p.Id == deleted.Page.Id)) return false;
            Directory.Move(trash, PageFolder(deleted.DocId, deleted.Page.Id));
            if (deleted.Page.State == PageState.Importing)
            {
                // Deleted while its photo was being copied: the import gave up on it, the copy may be partial.
                deleted.Page.State = PageState.Failed;
                deleted.Page.Error = "Ảnh bị xoá khi đang nhập; hãy nhập lại.";
            }
            doc.Pages.Insert(Math.Clamp(deleted.Index, 0, doc.Pages.Count), deleted.Page);
            SaveLocked(doc);
            return true;
        }
    }

    /// <summary>Takes out the placeholder pages (<see cref="PageState.Importing"/>) of an import that never finished: the
    /// app was killed with photos still waiting, and the picker's permission to read them died with it. Call once at
    /// start-up, before any import runs. Returns how many were removed.</summary>
    public int RemoveUnfinishedImports(string docId)
    {
        lock (_gate)
        {
            DocumentRecord? doc = GetLocked(docId);
            if (doc == null) return 0;
            List<PageRecord> left = doc.Pages.Where(p => p.State == PageState.Importing).ToList();
            if (left.Count == 0) return 0;
            doc.Pages.RemoveAll(p => p.State == PageState.Importing);
            SaveLocked(doc);
            foreach (PageRecord p in left)
            {
                try { if (Directory.Exists(PageFolder(docId, p.Id))) Directory.Delete(PageFolder(docId, p.Id), recursive: true); }
                catch (IOException) { }
            }
            return left.Count;
        }
    }

    /// <summary>Permanently removes trashed pages (called when leaving the document, and at start-up).</summary>
    public void EmptyTrash(string docId)
    {
        lock (_gate)
        {
            string folder = Path.Combine(DocumentFolder(docId), TrashName);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { } // a file still open: try again next time
        }
    }

    private const string TrashName = ".trash";

    private string TrashFolder(string docId, string pageId) => Path.Combine(DocumentFolder(docId), TrashName, pageId);

    public string DocumentFolder(string docId)
    {
        CheckId(docId);
        return Path.Combine(root, docId);
    }

    public string PageFolder(string docId, string pageId)
    {
        CheckId(pageId);
        return Path.Combine(DocumentFolder(docId), pageId);
    }

    public string OriginalPath(string docId, PageRecord page) =>
        Path.Combine(PageFolder(docId, page.Id), "original" + page.OriginalExtension);

    public string ProxyPath(string docId, PageRecord page) => Path.Combine(PageFolder(docId, page.Id), "proxy.jpg");

    public string ThumbPath(string docId, PageRecord page) => Path.Combine(PageFolder(docId, page.Id), "thumb.jpg");

    /// <summary>The straightened page of the given revision (".jpg" for color / gray, ".png" for black and white).</summary>
    public string CroppedPath(string docId, string pageId, int revision, string extension = ".jpg") =>
        Path.Combine(PageFolder(docId, pageId), $"cropped_{revision}{extension}");

    /// <summary>The page's current straightened render.</summary>
    public string CroppedPath(string docId, PageRecord page) =>
        CroppedPath(docId, page.Id, page.CroppedRevision, page.CroppedExtension);

    public string CroppedThumbPath(string docId, string pageId, int revision) =>
        Path.Combine(PageFolder(docId, pageId), $"cropped_thumb_{revision}.jpg");

    private DocumentRecord? GetLocked(string id)
    {
        if (_cache.TryGetValue(id, out DocumentRecord? cached)) return cached;
        DocumentRecord? doc = TryRead(Path.Combine(DocumentFolder(id), DocFileName));
        if (doc == null || doc.Id != id) return null;
        _cache[id] = doc;
        return doc;
    }

    private void SaveLocked(DocumentRecord doc)
    {
        string folder = DocumentFolder(doc.Id);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, DocFileName);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, DocumentJsonContext.Default.DocumentRecord));
        File.Move(tmp, path, overwrite: true);
    }

    private static DocumentRecord? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), DocumentJsonContext.Default.DocumentRecord);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Ids become folder names, so accept only what this store generates.</summary>
    private static void CheckId(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException($"Invalid id '{id}'.", nameof(id));
    }
}
