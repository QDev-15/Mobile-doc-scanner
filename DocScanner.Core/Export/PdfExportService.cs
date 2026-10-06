using ImageCoreService;

namespace DocScanner.Core;

/// <param name="Stage">What is happening ("Đang chuẩn bị trang...", "Đang ghi PDF...").</param>
public sealed record ExportProgress(string Stage, int Done, int Total);

/// <param name="SkippedPages">1-based numbers of pages left out (photo unreadable, or the page could not be straightened).</param>
public sealed record PdfExportResult(string Path, int PageCount, IReadOnlyList<int> SkippedPages, long Bytes);

/// <summary>
/// Exports a document as a PDF, one page per page in the document's order. Pages that are still
/// being imported, or whose straightened render is missing or stale, are finished first through the
/// background queue (so an export never races the queue on the same page); every page then goes into
/// the PDF as the file the app shows (JPEG or PNG), shrunk to the export quality first
/// (<see cref="ShrinkColorPagesAsync"/>).
/// </summary>
public sealed class PdfExportService(DocumentStore store, PageIngestQueue queue, IImageService images)
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);

    public async Task<PdfExportResult> ExportAsync(string docId, string outputPath,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default, PdfQuality? quality = null)
    {
        quality ??= PdfQuality.Medium;
        DocumentRecord doc = store.Get(docId) ?? throw new InvalidOperationException("Tài liệu không còn tồn tại.");
        if (store.Pages(docId).Count == 0) throw new InvalidOperationException("Tài liệu chưa có trang nào.");

        await FinishPagesAsync(docId, progress, ct);

        IReadOnlyList<PageRecord> pages = store.Pages(docId);
        var sources = new List<PdfPageSource>();
        var skipped = new List<int>();
        for (int i = 0; i < pages.Count; i++)
        {
            PageRecord p = pages[i];
            string file = p.CroppedRevision > 0 ? store.CroppedPath(docId, p) : "";
            if (p.State != PageState.Ready || p.NeedsRender || !File.Exists(file)) { skipped.Add(i + 1); continue; }
            sources.Add(PageLayout(p, file));
        }
        if (sources.Count == 0) throw new InvalidOperationException("Không có trang nào xuất được (ảnh lỗi hoặc chưa cắt được).");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        string tmp = outputPath + ".partial";
        string work = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, ".work_" + Guid.NewGuid().ToString("N"));
        try
        {
            sources = await ShrinkColorPagesAsync(sources, quality, work, progress, ct);
            await using (FileStream fs = File.Create(tmp))
            {
                // Forwarded synchronously: Progress<T> would post each report and could deliver them after the export returned.
                var written = new ForwardProgress(n => progress?.Report(new ExportProgress("Đang ghi PDF", n, sources.Count)));
                await ImagePdfWriter.WriteAsync(fs, sources, doc.Name, written, ct);
            }
            File.Move(tmp, outputPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
        return new PdfExportResult(outputPath, sources.Count, skipped, new FileInfo(outputPath).Length);
    }

    /// <summary>Re-encodes the JPEG (color / gray) pages at the export quality: shrunk to the quality's resolution
    /// (never enlarged) and saved at its JPEG quality into <paramref name="work"/>. Black-and-white pages (8-bit gray
    /// PNG, anti-aliased edges) are shrunk to <see cref="PdfQuality.GrayDpi"/> the same way, staying anti-aliased,
    /// except at <see cref="PdfQuality.High"/> (kept exactly as saved) and <see cref="PdfQuality.Small"/> (no
    /// <see cref="PdfQuality.GrayDpi"/>: becomes 1-bit instead, the smallest a page of text gets).</summary>
    private async Task<List<PdfPageSource>> ShrinkColorPagesAsync(List<PdfPageSource> sources, PdfQuality quality, string work,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var result = new List<PdfPageSource>(sources.Count);
        for (int i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress("Đang nén trang", i + 1, sources.Count));
            PdfPageSource page = sources[i];
            if (!IsJpeg(page.ImageFile))
            {
                result.Add(quality == PdfQuality.High ? page
                    : quality.GrayLongEdgePx is { } grayEdge ? await ShrinkGrayAsync(page, i, grayEdge, work, ct)
                    : await BilevelAsync(page, i, work, ct));
                continue;
            }

            Directory.CreateDirectory(work);
            string file = Path.Combine(work, $"{i}.jpg");
            RgbImage img = await images.LoadRgbAsync(page.ImageFile, quality.LongEdgePx, ct);
            await images.SaveJpegAsync(img, file, quality.JpegQuality, ct);
            result.Add(page with { ImageFile = file });
        }
        return result;
    }

    /// <summary>A smooth black-and-white page as 1-bit (dark below 128: the same strokes, without the soft edges).
    /// Used only for <see cref="PdfQuality.Small"/>, which has no <see cref="PdfQuality.GrayDpi"/>.</summary>
    private static async Task<PdfPageSource> BilevelAsync(PdfPageSource page, int index, string work, CancellationToken ct)
    {
        byte[] png = await File.ReadAllBytesAsync(page.ImageFile, ct);
        PngReader.PngData header = PngReader.Read(png);
        if (header.ColorType != 0 || header.BitDepth != 8) return page;
        byte[] bilevel = await Task.Run(() => PngWriter.EncodeBilevel(PngReader.DecodeGray8(png)), ct);
        Directory.CreateDirectory(work);
        string file = Path.Combine(work, $"{index}.png");
        await File.WriteAllBytesAsync(file, bilevel, ct);
        return page with { ImageFile = file };
    }

    /// <summary>A smooth black-and-white page shrunk to <paramref name="longEdge"/> px (never enlarged), staying an
    /// anti-aliased 8-bit gray PNG: unlike <see cref="BilevelAsync"/>, the soft stroke edges survive, so the page still
    /// looks smooth zoomed in, not a pixel staircase.</summary>
    private static async Task<PdfPageSource> ShrinkGrayAsync(PdfPageSource page, int index, int longEdge, string work, CancellationToken ct)
    {
        byte[] png = await File.ReadAllBytesAsync(page.ImageFile, ct);
        PngReader.PngData header = PngReader.Read(png);
        if (header.ColorType != 0 || header.BitDepth != 8) return page; // already 1-bit (an old page): nothing to shrink
        GrayImage gray = await Task.Run(() => PngReader.DecodeGray8(png), ct);
        double scale = Math.Min(1.0, (double)longEdge / Math.Max(gray.Width, gray.Height));
        if (scale < 1)
        {
            int w = Math.Max(1, (int)Math.Round(gray.Width * scale)), h = Math.Max(1, (int)Math.Round(gray.Height * scale));
            gray = await Task.Run(() => gray.Resize(w, h), ct);
        }
        Directory.CreateDirectory(work);
        string file = Path.Combine(work, $"{index}.png");
        await File.WriteAllBytesAsync(file, PngWriter.EncodeGray8(gray), ct);
        return page with { ImageFile = file };
    }

    private static bool IsJpeg(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return fs.ReadByte() == 0xFF && fs.ReadByte() == 0xD8;
    }

    /// <summary>Where a page goes in the PDF. A4 mode: an A4 sheet (portrait or landscape like the render); a render that
    /// is not A4-shaped (an outline that was not a whole sheet, kept undistorted by <see cref="CropPlanner"/>) is fitted
    /// and centered on it with white margins, never stretched. Free mode: the render's own proportions, long side = A4's.</summary>
    public static PdfPageSource PageLayout(PageRecord p, string file)
    {
        int w = Math.Max(1, p.CroppedWidth), h = Math.Max(1, p.CroppedHeight);
        if (p.CroppedFreeAspect)
        {
            double k = ImagePdfWriter.A4LongPt / Math.Max(w, h);
            return new PdfPageSource(file, w * k, h * k);
        }

        (double pw, double ph) = w > h ? (ImagePdfWriter.A4LongPt, ImagePdfWriter.A4ShortPt) : (ImagePdfWriter.A4ShortPt, ImagePdfWriter.A4LongPt);
        double fit = Math.Min(pw / w, ph / h);
        double iw = w * fit, ih = h * fit;
        if (Math.Abs(iw - pw) < 1 && Math.Abs(ih - ph) < 1) return new PdfPageSource(file, pw, ph); // A4 render: fills the sheet
        return new PdfPageSource(file, pw, ph) { ImageRect = ((pw - iw) / 2, (ph - ih) / 2, iw, ih) };
    }

    /// <summary>Waits until every page is either finished and rendered, or failed.</summary>
    private async Task FinishPagesAsync(string docId, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        int total = store.Pages(docId).Count;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<PageRecord> pages = store.Pages(docId);
            int waiting = 0;
            foreach (PageRecord p in pages)
            {
                if (p.State == PageState.Failed) continue;
                if (p.State != PageState.Ready) { waiting++; continue; } // import still running: the queue will finish it
                if (!p.NeedsRender || p.RenderError != null) continue;
                waiting++;
                if (!queue.IsPreparing(p.Id)) queue.EnqueueRender(docId, p.Id); // promotes a waiting prerender; never queued twice
            }
            progress?.Report(new ExportProgress("Đang chuẩn bị trang", total - waiting, total));
            if (waiting == 0) return;
            await Task.Delay(Poll, ct);
        }
    }

    /// <summary>A file name for the document ("Hóa đơn 03/2026" -> "Hóa đơn 03-2026.pdf").</summary>
    public static string FileNameFor(string documentName)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var chars = documentName.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray();
        string name = new string(chars).Trim().Trim('.');
        if (name.Length > 80) name = name[..80].Trim();
        return (name.Length == 0 ? "Tai lieu" : name) + ".pdf";
    }
}

internal sealed class ForwardProgress(Action<int> report) : IProgress<int>
{
    public void Report(int value) => report(value);
}
