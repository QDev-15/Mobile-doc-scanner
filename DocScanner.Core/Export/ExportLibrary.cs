namespace DocScanner.Core;

/// <summary>An exported PDF on disk.</summary>
public sealed record ExportedFile(string Path, string Name, long Bytes, DateTime Created);

/// <summary>
/// The exported PDFs, kept in the app's own data folder (not the cache, which Android may clear) so the
/// "PDF đã xuất" screen can list them later. Each export gets its own file named after the document
/// and the time, so exporting again never overwrites an earlier PDF.
/// </summary>
public sealed class ExportLibrary(string folder)
{
    public string Folder => folder;

    /// <summary>A free path for a new export of <paramref name="documentName"/>: "Hợp đồng 2026-09-26 14.05.pdf",
    /// with " (2)", " (3)"... when that minute is already taken.</summary>
    public string NewPath(string documentName, DateTime now)
    {
        Directory.CreateDirectory(folder);
        string stem = Path.GetFileNameWithoutExtension(PdfExportService.FileNameFor(documentName)) + " " + now.ToString("yyyy-MM-dd HH.mm");
        string path = Path.Combine(folder, stem + ".pdf");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}).pdf");
        return path;
    }

    /// <summary>All exported PDFs, newest first (unfinished ".partial" files are not listed).</summary>
    public IReadOnlyList<ExportedFile> List()
    {
        if (!Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder).EnumerateFiles("*.pdf")
            .Select(f => new ExportedFile(f.FullName, Path.GetFileNameWithoutExtension(f.Name), f.Length, f.LastWriteTime))
            .OrderByDescending(f => f.Created)
            .ToList();
    }

    /// <summary>Deletes one export (only files inside the library folder).</summary>
    public bool Delete(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            return false;
        File.Delete(full);
        return true;
    }
}
