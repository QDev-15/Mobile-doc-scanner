using ImageCoreService;

namespace DocScanner.Core;

/// <summary>User edits of a page: the paper outline and the rotation. Changes go through the store
/// (saved at once) and, for a rotation, back through the background pipeline that remakes the
/// thumbnail and proxy.</summary>
public sealed class PageEditService(DocumentStore store, PageIngestQueue queue, CropDetectionService detection)
{
    /// <summary>Records an outline chosen by hand (normalized, TL TR BR BL). Automatic detection will
    /// not replace it afterwards.</summary>
    public bool SetCrop(string docId, string pageId, Quad quad) =>
        Change(docId, pageId, p =>
        {
            p.CropQuad = quad.ToValues();
            p.CropBend = null; // an outline moved by hand is straight (the bulge belonged to the detected border)
            p.CropManual = true;
        });

    /// <summary>Chooses the shape of the straightened page: A4 (default) or the outline's own proportions.
    /// The page then needs a new render.</summary>
    public bool SetFreeAspect(string docId, string pageId, bool free) =>
        Change(docId, pageId, p => p.FreeAspect = free);

    /// <summary>Changes how the page looks (color / gray / black and white, darkness, background
    /// cleaning). Arguments left null keep their current value. The page then needs a new render.</summary>
    public bool SetFilter(string docId, string pageId, PageColorMode? mode = null, int? darkness = null, bool? cleanBackground = null,
        int? brightness = null, int? contrast = null) =>
        Change(docId, pageId, p =>
        {
            if (mode != null) p.ColorMode = mode.Value;
            if (darkness != null) p.BwDarkness = Math.Clamp(darkness.Value, 0, 100);
            if (cleanBackground != null) p.CleanBackground = cleanBackground.Value;
            if (brightness != null) p.Brightness = Math.Clamp(brightness.Value, -100, 100);
            if (contrast != null) p.Contrast = Math.Clamp(contrast.Value, -100, 100);
        });

    /// <summary>Gives every page of the document the same look. Returns the ids of the pages whose look
    /// changed (the ones that need a new render).</summary>
    public IReadOnlyList<string> ApplyFilterToAll(string docId, FilterOptions filter)
    {
        var changed = new List<string>();
        store.Update(docId, d =>
        {
            foreach (PageRecord p in d.Pages)
            {
                bool same = FilterOptions.SameLook(p.Filter, filter);
                // Copy every value (not only the visible ones) so a later mode switch behaves the same on all pages.
                p.ColorMode = filter.Mode;
                p.BwDarkness = Math.Clamp(filter.Darkness, 0, 100);
                p.CleanBackground = filter.CleanBackground;
                p.Brightness = Math.Clamp(filter.Tone.Brightness, -100, 100);
                p.Contrast = Math.Clamp(filter.Tone.Contrast, -100, 100);
                if (!same) changed.Add(p.Id);
            }
        });
        return changed;
    }

    /// <summary>The outline is the whole picture.</summary>
    public bool UseFullImage(string docId, string pageId) => SetCrop(docId, pageId, Quad.Full);

    /// <summary>Runs the detector again and replaces the outline, even a hand-made one.</summary>
    public Task<QuadDetection?> RedetectAsync(string docId, string pageId, CancellationToken ct = default) =>
        detection.DetectAsync(docId, pageId, ct, overrideManual: true);

    /// <summary>Turns the page clockwise by a multiple of 90 degrees. The outline is turned with it;
    /// the thumbnail and proxy are rebuilt in the background (the page is Pending until then).
    /// Only a finished page can be rotated: returns false otherwise.</summary>
    public bool Rotate(string docId, string pageId, int clockwiseDegrees = 90)
    {
        bool rotated = false;
        store.Update(docId, d =>
        {
            PageRecord? p = d.Pages.FirstOrDefault(x => x.Id == pageId);
            if (p == null || p.State != PageState.Ready) return;

            int turns = (((clockwiseDegrees / 90) % 4) + 4) % 4;
            if (turns == 0) return;
            for (int i = 0; i < turns; i++)
            {
                if (p.CropQuad != null) p.CropQuad = ImageGeometry.RotateQuadClockwise(Quad.FromValues(p.CropQuad)).ToValues();
                if (PageBends.FromValues(p.CropBend) is { } b) p.CropBend = b.RotateClockwise().ToValues();
                (p.ProxyWidth, p.ProxyHeight) = (p.ProxyHeight, p.ProxyWidth);
            }
            p.UserRotation = (p.UserRotation + turns * 90) % 360;
            p.State = PageState.Pending;
            rotated = true;
        });
        if (rotated) queue.Enqueue(docId, pageId);
        return rotated;
    }

    /// <summary>Turns the straightened page (not the photo, not the outline) by a multiple of 90 degrees, e.g. from the
    /// result screen. The page then needs a new render; nothing else is rebuilt, so the page stays editable.</summary>
    public bool RotateOutput(string docId, string pageId, int clockwiseDegrees = 90) =>
        Change(docId, pageId, p =>
        {
            p.OutputRotation = (((p.OutputRotation + clockwiseDegrees) % 360) + 360) % 360 / 90 * 90;
            // Signatures stay where they are on the paper: they turn with the page.
            p.Stamps = p.Stamps?.Select(s => s.Rotate(clockwiseDegrees / 90)).ToList();
        });

    /// <summary>The signatures on a page (null / empty: none); the page is rendered again with them.</summary>
    public bool SetStamps(string docId, string pageId, IReadOnlyList<PageStamp>? stamps) =>
        Change(docId, pageId, p => p.Stamps = stamps is { Count: > 0 } ? [.. stamps] : null);

    private bool Change(string docId, string pageId, Action<PageRecord> change)
    {
        bool found = false;
        store.Update(docId, d =>
        {
            PageRecord? p = d.Pages.FirstOrDefault(x => x.Id == pageId);
            if (p == null) return;
            change(p);
            found = true;
        });
        return found;
    }
}
