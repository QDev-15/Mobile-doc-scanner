using ImageCoreService;

namespace DocScanner.Core;

/// <summary>
/// Makes the straightened page from the ORIGINAL photo (never from the screen proxy, so the result
/// keeps the photo's full resolution up to the A4 / 300 DPI cap). The outline is stored on the
/// upright picture; it is mapped back to the stored, possibly rotated file with the page's effective
/// orientation, only the part of the photo holding the page is decoded (at a power-of-two
/// shrink that still gives the output resolution), and that part is warped into a rectangle.
/// </summary>
public sealed class CropRenderService(DocumentStore store, IImageService images, Signatures.SignatureLibrary? signatures = null)
{
    public const int JpegQuality = 94;
    public const int ThumbEdge = 512;
    private const int ThumbQuality = 85;

    /// <summary>Renders the page and records the result. No-op for a page that is not ready or is gone.</summary>
    public async Task RenderAsync(string docId, string pageId, CancellationToken ct = default)
    {
        PageRecord? page = store.Pages(docId).FirstOrDefault(p => p.Id == pageId);
        if (page == null || page.State != PageState.Ready || page.RawWidth <= 0 || page.RawHeight <= 0) return;

        // What the render is for: remembered so a later edit is recognised as making it stale.
        double[] quadValues = page.CropQuad ?? Quad.Inset(0.03).ToValues();
        double[]? bendValues = page.CropBend;
        int rotation = page.UserRotation;
        int outputRotation = page.OutputRotation;
        bool freeAspect = page.FreeAspect;
        FilterOptions filter = page.Filter;
        List<PageStamp>? stamps = page.Stamps is { Count: > 0 } ? [.. page.Stamps] : null;

        // The user may change the page again while it renders (a slider nudged twice, a second turn): a render that is
        // out of date by then stops at the next check instead of finishing work nobody will see, and leaves the CPU
        // to the screen and to the render that replaces it. The queue treats the cancellation as "not done", not "failed".
        void ThrowIfStale()
        {
            ct.ThrowIfCancellationRequested();
            PageRecord? now = store.Pages(docId).FirstOrDefault(p => p.Id == pageId);
            bool current = now != null && now.State == PageState.Ready
                && now.UserRotation == rotation && now.OutputRotation == outputRotation && now.FreeAspect == freeAspect
                && (now.CropQuad ?? Quad.Inset(0.03).ToValues()).AsSpan().SequenceEqual(quadValues)
                && PageRecord.SameBends(now.CropBend, bendValues)
                && FilterOptions.SameLook(now.Filter, filter)
                && PageRecord.SameStamps(now.Stamps, stamps);
            if (!current) throw new OperationCanceledException("The page changed while it was being rendered.");
        }

        FilteredPage result = await Task.Run(async () =>
        {
            // Straighten, turn, then filter: the same order as the result screen's preview.
            RgbImage flat = Level((await WarpAsync(docId, page, quadValues, bendValues, CropPlanner.MaxLongEdge, ct)).RotateClockwise(outputRotation / 90));
            ThrowIfStale();
            FilteredPage filtered;
            using (Perf.Measure($"render filter {filter.Mode} {flat.Width}x{flat.Height}"))
                filtered = DocumentFilter.Apply(flat, filter, PageDpi(flat.Width, flat.Height));
            DrawSignatures(filtered, stamps);
            return filtered;
        }, ct);
        ThrowIfStale();
        Perf.Scope saving = Perf.Measure("render save");

        int revision = page.CroppedRevision + 1;
        string extension = result.IsBlackWhite ? ".png" : ".jpg";
        string flatPath = store.CroppedPath(docId, pageId, revision, extension);
        string thumbPath = store.CroppedThumbPath(docId, pageId, revision);
        Directory.CreateDirectory(Path.GetDirectoryName(flatPath)!);
        if (result.Color != null)
            await images.SaveJpegAsync(result.Color, flatPath, JpegQuality, ct);
        else if (result.IsBilevel)
            await File.WriteAllBytesAsync(flatPath, PngWriter.EncodeBilevel(result.Gray!), ct); // lossless, tiny, embeds straight into PDF
        else if (result.IsBlackWhite)
            await File.WriteAllBytesAsync(flatPath, PngWriter.EncodeGray8(result.Gray!), ct); // anti-aliased edges; PDF export shrinks it (Vừa / Cao) or bilevels it (Nhỏ)
        else
            await images.SaveJpegAsync(RgbImage.FromGray(result.Gray!), flatPath, JpegQuality, ct);
        await images.SaveJpegAsync(MakeThumb(result), thumbPath, ThumbQuality, ct);
        saving.Dispose();
        int outWidth = result.Width, outHeight = result.Height;

        int previous = 0;
        string previousExtension = ".jpg";
        bool kept = store.Update(docId, d =>
        {
            PageRecord? p = d.Pages.FirstOrDefault(x => x.Id == pageId);
            if (p == null) return;
            previous = p.CroppedRevision;
            previousExtension = p.CroppedExtension;
            p.CroppedRevision = revision;
            p.CroppedExtension = extension;
            p.CroppedWidth = outWidth;
            p.CroppedHeight = outHeight;
            p.CroppedQuad = quadValues;
            p.CroppedBend = bendValues;
            p.CroppedStamps = stamps;
            p.CroppedGeometry = PageRecord.GeometryVersion;
            p.CroppedBlackWhiteVersion = PageRecord.BlackWhiteVersion;
            p.CroppedRotation = rotation;
            p.CroppedOutputRotation = outputRotation;
            p.CroppedFreeAspect = freeAspect;
            p.CroppedColorMode = filter.Mode;
            p.CroppedBwDarkness = filter.Darkness;
            p.CroppedCleanBackground = filter.CleanBackground;
            p.CroppedBrightness = filter.Tone.Brightness;
            p.CroppedContrast = filter.Tone.Contrast;
            p.RenderError = null;
        });

        // Old renders are only garbage once the new one is recorded.
        if (kept && previous > 0)
            DeleteQuietly(store.CroppedPath(docId, pageId, previous, previousExtension), store.CroppedThumbPath(docId, pageId, previous));
        if (!kept) DeleteQuietly(flatPath, thumbPath); // the page was deleted meanwhile
    }

    /// <summary>Draws the page's signatures into the filtered page (in place).</summary>
    public void DrawSignatures(FilteredPage page, IReadOnlyList<PageStamp>? stamps)
    {
        if (stamps is not { Count: > 0 } || signatures == null) return;
        if (page.Color != null) Signatures.Stamper.Apply(page.Color, stamps, signatures.Ink);
        else Signatures.Stamper.Apply(page.Gray!, stamps, signatures.Ink, page.IsBilevel);
    }

    /// <summary>The straightened page at screen size (<see cref="CropPlanner.PreviewLongEdge"/>), without any filter: what
    /// the result screen re-filters live when the user switches color / gray / black and white. Same geometry as the
    /// saved page, from the same original, at a coarser decode (a fraction of a second on a phone). Null when the page
    /// is not ready.</summary>
    public async Task<RgbImage?> RenderPreviewAsync(string docId, string pageId, int maxLongEdge = CropPlanner.PreviewLongEdge,
        CancellationToken ct = default)
    {
        PageRecord? page = store.Pages(docId).FirstOrDefault(p => p.Id == pageId);
        if (page == null || page.State != PageState.Ready || page.RawWidth <= 0 || page.RawHeight <= 0) return null;

        // Fast path: a saved render with this geometry and no filter already is the straightened page. Decoding it
        // (one JPEG, ~0.1 s) beats decoding the photo region and warping it again (~0.6-1.2 s on a phone), and pages are
        // rendered in color in the background right after import, so this is the usual case.
        if (page.HasPlainColorRender)
        {
            string file = store.CroppedPath(docId, page);
            if (File.Exists(file))
            {
                try
                {
                    using (Perf.Measure("preview from saved render"))
                        return await images.LoadRgbAsync(file, maxLongEdge, ct);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    // Unreadable file: straighten from the photo instead.
                }
            }
        }

        double[] quadValues = page.CropQuad ?? Quad.Inset(0.03).ToValues();
        int turns = page.OutputRotation / 90;
        double[]? bendValues = page.CropBend;
        return await Task.Run(async () => Level((await WarpAsync(docId, page, quadValues, bendValues, maxLongEdge, ct)).RotateClockwise(turns)), ct);
    }

    /// <summary>The straightened, upright page with its text lines made level (<see cref="ContentAligner"/>): the outline
    /// is never perfect, and a line of text climbing across the page shows at once.</summary>
    private static RgbImage Level(RgbImage page)
    {
        using (Perf.Measure($"level text lines {page.Width}x{page.Height}"))
            return ContentAligner.Align(page, ContentAligner.Measure(page));
    }

    /// <summary>Decodes only the part of the original that holds the page (outline mapped back to the stored, possibly
    /// rotated file) and warps it into a rectangle whose long edge is at most <paramref name="maxLongEdge"/>.</summary>
    private async Task<RgbImage> WarpAsync(string docId, PageRecord page, double[] quadValues, double[]? bendValues, int maxLongEdge,
        CancellationToken ct)
    {
        Quad upright = Quad.FromValues(quadValues);
        Quad stored = ImageGeometry.UprightToStored(upright, page.EffectiveOrientation).Scale(page.RawWidth, page.RawHeight);
        // Bends are relative to each side and measured from the page middle: they hold unchanged in the stored, turned
        // or mirrored, pixel frame of the original.
        PageBends? bends = PageBends.FromValues(bendValues);
        CropPlan plan = CropPlanner.Plan(stored, page.RawWidth, page.RawHeight, page.FreeAspect ? CropAspect.Free : CropAspect.A4, maxLongEdge, bends);

        RgbImage region;
        using (Perf.Measure($"decode region {plan.RegionWidth}x{plan.RegionHeight} /{plan.Sample}"))
            region = await images.LoadRegionAsync(store.OriginalPath(docId, page),
                plan.RegionX, plan.RegionY, plan.RegionWidth, plan.RegionHeight, plan.Sample, ct);

        // The decoder may round the region size; use its real scale.
        double kx = (double)region.Width / plan.RegionWidth, ky = (double)region.Height / plan.RegionHeight;
        PointD Local(PointD p) => new((p.X - plan.RegionX) * kx, (p.Y - plan.RegionY) * ky);
        var source = new Quad(Local(stored.TopLeft), Local(stored.TopRight), Local(stored.BottomRight), Local(stored.BottomLeft));
        ct.ThrowIfCancellationRequested();
        using (Perf.Measure($"warp -> {plan.OutWidth}x{plan.OutHeight}"))
            return PerspectiveWarp.Warp(region, source, plan.OutWidth, plan.OutHeight, bends);
    }

    /// <summary>Resolution of a straightened page, taking its long side as an A4 sheet's (11.69 in). Exact
    /// for A4 renders; for free-aspect pages it only sizes the black-and-white window and specks, where
    /// being within a factor of two is plenty.</summary>
    public static int PageDpi(int width, int height) => Math.Max(50, (int)Math.Round(Math.Max(width, height) / 11.69));

    private static RgbImage MakeThumb(FilteredPage page)
    {
        (int tw, int th) = ImageGeometry.FitLongEdge(page.Width, page.Height, ThumbEdge);
        if (page.Color != null) return page.Color.Resize(tw, th);
        // Shrink the gray page first so only a small RGB copy is ever made.
        int factor = Math.Max(1, Math.Min(page.Width / tw, page.Height / th));
        return RgbImage.FromGray(page.Gray!.Downscale(factor)).Resize(tw, th);
    }

    private static void DeleteQuietly(params string[] paths)
    {
        foreach (string p in paths)
        {
            try { File.Delete(p); }
            catch (IOException) { }
        }
    }
}
