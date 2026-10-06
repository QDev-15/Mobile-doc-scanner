using System.Text.Json.Serialization;
using ImageCoreService;

namespace DocScanner.Core;

/// <summary>
/// Where a page is in the background pipeline. The page shows up in the UI the moment it is
/// added; the expensive work follows in stages, each one visible as soon as it is done.
/// </summary>
public enum PageState
{
    /// <summary>Original copied into the document; nothing derived yet.</summary>
    Pending,
    /// <summary>Thumbnail exists (size and EXIF are known); the screen proxy is still being made.</summary>
    Preview,
    /// <summary>Proxy exists: the page can be opened. (The paper outline may still be coming.)</summary>
    Ready,
    /// <summary>The photo could not be decoded (see <see cref="PageRecord.Error"/>).</summary>
    Failed,
    /// <summary>A placeholder: the photo was picked and holds its place in the document, but has not been copied in yet
    /// (<see cref="BackgroundImporter"/>). No file exists; nothing can be done with the page until it becomes
    /// <see cref="Pending"/>. Placeholders left by an app that was killed are removed at the next start.</summary>
    Importing,
}

/// <summary>
/// One scanned page. The original photo is kept byte-for-byte on disk (full resolution,
/// EXIF untouched); everything shown on screen comes from the small derived files.
/// </summary>
public sealed class PageRecord
{
    public string Id { get; set; } = "";

    /// <summary>Documents written before this field existed had only finished pages, hence the default.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PageState>))]
    public PageState State { get; set; } = PageState.Ready;

    /// <summary>Why the page is <see cref="PageState.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>File extension of the stored original (".jpg", ".png", ".heic"...).</summary>
    public string OriginalExtension { get; set; } = ".jpg";

    /// <summary>Pixel size of the original exactly as stored in the file, i.e. BEFORE the
    /// EXIF orientation is applied.</summary>
    public int RawWidth { get; set; }
    public int RawHeight { get; set; }

    /// <summary>EXIF orientation tag 1..8 of the original file (1 = as stored).</summary>
    public int ExifOrientation { get; set; } = 1;

    /// <summary>Extra rotation chosen by the user, in degrees clockwise (0, 90, 180 or 270), for
    /// photos whose EXIF orientation is wrong or missing.</summary>
    public int UserRotation { get; set; }

    /// <summary>The orientation that makes the page upright: the file's EXIF tag combined with
    /// <see cref="UserRotation"/>. Everything that reads the original at full resolution (the
    /// perspective crop) must use this, not <see cref="ExifOrientation"/>.</summary>
    [JsonIgnore]
    public int EffectiveOrientation => ImageGeometry.ComposeRotation(ExifOrientation, UserRotation);

    /// <summary>Size of the upright (orientation applied) screen proxy.</summary>
    public int ProxyWidth { get; set; }
    public int ProxyHeight { get; set; }

    /// <summary>Paper outline as 8 numbers x0,y0..x3,y3 (TL, TR, BR, BL) normalized to 0..1 of
    /// the upright image, so it applies to the proxy and to the full-size original alike.
    /// Null until detection has run.</summary>
    public double[]? CropQuad { get; set; }

    /// <summary>0..1 score of the automatic detection.</summary>
    public double CropConfidence { get; set; }

    /// <summary>True once the user has moved the outline by hand: automatic detection then leaves it alone.</summary>
    public bool CropManual { get; set; }

    /// <summary>True when the detector really found a sheet; false when <see cref="CropQuad"/>
    /// is just the whole frame (the fallback).</summary>
    public bool CropDetected { get; set; }

    /// <summary>How outlines are detected now: 2 = the refined border is the steepest point of the paper's edge, not its
    /// outer end; a side is fitted from a consensus line and only bulges where the evidence reaches both corners; a corner
    /// the evidence does not reach stays with the detector (and within 1% of the frame); a sheet the frame cuts across a
    /// corner is found. Automatic outlines by older rules (1 = before, corners 2-6% outside the sheet on the owner's
    /// photos) are detected again once, in the background; outlines moved by hand are kept.</summary>
    public const int DetectionVersion = 2;

    /// <summary>The <see cref="DetectionVersion"/> the automatic outline was found with (0 before this was recorded).</summary>
    public int CropDetection { get; set; }

    /// <summary>An automatic outline found by older detection rules (see <see cref="DetectionVersion"/>).</summary>
    [JsonIgnore]
    public bool NeedsDetection => CropQuad == null || (!CropManual && CropDetection < DetectionVersion);

    /// <summary>Revision of the straightened page made from the original (0 = none yet). The files are
    /// named after it, so a new render never shows a stale cached picture.</summary>
    public int CroppedRevision { get; set; }

    public int CroppedWidth { get; set; }
    public int CroppedHeight { get; set; }

    /// <summary>The outline and rotation the current render was made from.</summary>
    public double[]? CroppedQuad { get; set; }
    public int CroppedRotation { get; set; }

    /// <summary>Turn of the straightened page, in degrees clockwise (0, 90, 180, 270), chosen on the result screen. Unlike
    /// <see cref="UserRotation"/> it does not touch the photo or the outline: the finished page is simply turned (a
    /// landscape table on a portrait sheet...).</summary>
    public int OutputRotation { get; set; }

    /// <summary>The <see cref="OutputRotation"/> the current render was made with.</summary>
    public int CroppedOutputRotation { get; set; }

    /// <summary>False (default) = the straightened page is an A4 sheet; true = it keeps the proportions of the outline.</summary>
    public bool FreeAspect { get; set; }

    /// <summary>The aspect the current render was made with.</summary>
    public bool CroppedFreeAspect { get; set; }

    /// <summary>Why the last render failed, if it did.</summary>
    public string? RenderError { get; set; }

    /// <summary>Color / gray / black-and-white. Pages from before this setting existed are color (that is what the old
    /// code always rendered): this default stays <see cref="PageColorMode.Color"/> so a doc.json missing the field
    /// entirely still reads as the color page it was actually rendered as -- see
    /// <c>A_document_saved_before_page_looks_existed_still_reads_as_rendered_color_pages</c>. A newly imported page
    /// gets black-and-white explicitly, in <see cref="ImportService"/> (owner's request 2026-09-29: most scans are
    /// documents, not photos, and black-and-white is both the crisper look and the much smaller file -- color is one
    /// tap away on the filter cards for the pages that do need it), not through this default.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PageColorMode>))]
    public PageColorMode ColorMode { get; set; } = PageColorMode.Color;

    /// <summary>0..100: black-and-white threshold strength (50 = default).</summary>
    public int BwDarkness { get; set; } = FilterOptions.DefaultDarkness;

    /// <summary>Flatten shadows / yellowed paper in gray and black-and-white modes.</summary>
    public bool CleanBackground { get; set; } = true;

    /// <summary>-100..100, color and gray pages (see <see cref="ToneAdjust"/>).</summary>
    public int Brightness { get; set; }
    public int Contrast { get; set; }

    /// <summary>The look the current render was made with.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PageColorMode>))]
    public PageColorMode CroppedColorMode { get; set; } = PageColorMode.Color;
    public int CroppedBwDarkness { get; set; } = FilterOptions.DefaultDarkness;
    public bool CroppedCleanBackground { get; set; } = true;
    public int CroppedBrightness { get; set; }
    public int CroppedContrast { get; set; }

    /// <summary>File type of the current render: ".jpg" (color / gray) or ".png" (black and white).</summary>
    public string CroppedExtension { get; set; } = ".jpg";

    [JsonIgnore]
    public FilterOptions Filter => new(ColorMode, BwDarkness, CleanBackground) { Tone = new ToneAdjust(Brightness, Contrast) };

    /// <summary>The look of the current render.</summary>
    [JsonIgnore]
    public FilterOptions CroppedFilter =>
        new(CroppedColorMode, CroppedBwDarkness, CroppedCleanBackground) { Tone = new ToneAdjust(CroppedBrightness, CroppedContrast) };

    /// <summary>True when there is no render yet, or the outline / rotation / look changed since the last one.</summary>
    [JsonIgnore]
    public bool NeedsRender =>
        CroppedRevision == 0
        || CroppedRotation != UserRotation
        || CroppedOutputRotation != OutputRotation
        || CroppedFreeAspect != FreeAspect
        || RenderIsOutdated
        || (CropQuad != null && (CroppedQuad == null || !CropQuad.AsSpan().SequenceEqual(CroppedQuad)))
        || !SameBends(CropBend, CroppedBend)
        || !SameStamps(Stamps, CroppedStamps)
        || !FilterOptions.SameLook(Filter, CroppedFilter);

    /// <summary>The saved render is the straightened page as it stands now (same outline, rotations and shape) with no
    /// filter at all (color, neutral brightness / contrast): it can serve as the result screen's unfiltered preview.</summary>
    [JsonIgnore]
    public bool HasPlainColorRender =>
        CroppedRevision > 0
        && CroppedColorMode == PageColorMode.Color && CroppedBrightness == 0 && CroppedContrast == 0
        && CroppedRotation == UserRotation && CroppedOutputRotation == OutputRotation && CroppedFreeAspect == FreeAspect
        && CropQuad != null && CroppedQuad != null && CropQuad.AsSpan().SequenceEqual(CroppedQuad)
        && SameBends(CropBend, CroppedBend)
        && (CroppedStamps == null || CroppedStamps.Count == 0) // the preview draws the signatures itself
        && !RenderIsOutdated;

    /// <summary>How pages are shaped and straightened now: 3 = perspective corrected with a phone camera's focal length,
    /// every sheet-shaped outline exactly A4 in A4 mode (<see cref="PageGeometry.OutputAspect"/>), curved sides
    /// (<see cref="CropBend"/>). Renders made by older rules are redone once, in the background: 1 = side-length
    /// proportions, straight sides only; 2 = focal length measured from the outline, which made real A4 pages 1.57 to 3.07
    /// : 1 long (owner's "Tài liệu 1", pages 4, 6, 7, 8).</summary>
    public const int GeometryVersion = 5; // 5: ContentAligner no longer follows the 4 measured bands exactly (piecewise --
    // could ripple a straight edge on noisy band measurements, owner's "T1" report 2026-09-29); only ever a straight-line
    // fit now. 4: text lines levelled after straightening (ContentAligner).

    /// <summary>The rules the current render was made with (<see cref="GeometryVersion"/>); 0 for renders from before
    /// this was recorded.</summary>
    public int CroppedGeometry { get; set; }

    /// <summary>How black-and-white pages are made: 2 = sharpened before the threshold, anti-aliased stroke edges
    /// (<see cref="DocumentFilter"/>). Black-and-white renders by older rules (1 = plain 1-bit Sauvola, which dropped thin
    /// strokes and diacritics of phone photos) are redone once; color and gray pages are not affected.</summary>
    public const int BlackWhiteVersion = 2;

    /// <summary>The <see cref="BlackWhiteVersion"/> the current render was made with (0 before this was recorded).</summary>
    public int CroppedBlackWhiteVersion { get; set; }

    /// <summary>A render made by older straightening rules (see <see cref="GeometryVersion"/>), or an older black and white.</summary>
    [JsonIgnore]
    public bool RenderIsOutdated => CroppedRevision > 0
        && (CroppedGeometry < GeometryVersion
            || (CroppedColorMode == PageColorMode.BlackWhite && CroppedBlackWhiteVersion < BlackWhiteVersion));

    /// <summary>Curved sides of a page that was not lying flat (<see cref="PageBends"/>, 8 numbers), found with the
    /// outline; null = straight sides. Cleared when the user moves the outline by hand.</summary>
    public double[]? CropBend { get; set; }

    /// <summary>The bends the current render was made with.</summary>
    public double[]? CroppedBend { get; set; }

    [JsonIgnore]
    public PageBends? Bends => PageBends.FromValues(CropBend);

    /// <summary>Signatures placed on the page (in the finished page's frame, see <see cref="PageStamp"/>); null or empty =
    /// none. They are drawn into every render, so they end up in the PDF.</summary>
    public List<PageStamp>? Stamps { get; set; }

    /// <summary>The signatures the current render was made with.</summary>
    public List<PageStamp>? CroppedStamps { get; set; }

    internal static bool SameStamps(IReadOnlyList<PageStamp>? a, IReadOnlyList<PageStamp>? b) =>
        (a == null || a.Count == 0) ? (b == null || b.Count == 0) : b != null && a.SequenceEqual(b);

    internal static bool SameBends(double[]? a, double[]? b) =>
        (a == null || a.All(v => v == 0)) ? (b == null || b.All(v => v == 0)) : b != null && a.AsSpan().SequenceEqual(b);
    /// <summary>Upright size of the original, in pixels.
    [JsonIgnore]
    public (int Width, int Height) UprightSize => ImageGeometry.UprightSize(RawWidth, RawHeight, EffectiveOrientation);
}

/// <summary>
/// A signature placed on a page, in the frame of the finished page (after <see cref="PageRecord.OutputRotation"/>):
/// center at (<see cref="CenterX"/>, <see cref="CenterY"/>) in 0..1 of the page's width / height, width of the signature
/// (in its own frame) <see cref="Size"/> x the page's short edge, the signature picture turned <see cref="Turns"/> quarter
/// turns clockwise. Relative to the short edge, so the signature keeps its size when the page is turned.
/// </summary>
public sealed record PageStamp(string SignatureId, double CenterX, double CenterY, double Size, int Turns = 0)
{
    /// <summary>The same signature after the page was turned by <paramref name="quarterTurns"/> clockwise.</summary>
    public PageStamp Rotate(int quarterTurns)
    {
        int t = ((quarterTurns % 4) + 4) % 4;
        double x = CenterX, y = CenterY;
        for (int i = 0; i < t; i++) (x, y) = (1 - y, x);
        return this with { CenterX = x, CenterY = y, Turns = (Turns + t) % 4 };
    }
}

public sealed class DocumentRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<PageRecord> Pages { get; set; } = [];

    /// <summary>The folder the document is in (<see cref="FolderRecord.Id"/>), or null for the top level.</summary>
    public string? FolderId { get; set; }
}

/// <summary>A folder on the main screen: documents are filed into it (one level, no folders inside folders).</summary>
/// <summary>A folder may sit inside another (<see cref="ParentFolderId"/>, null = top level) -- any depth, no
/// limit. <see cref="DocumentStore"/> is what enforces the two invariants that keep this safe: a folder can
/// never become its own ancestor (<see cref="DocumentStore.MoveFolder"/>), and deleting one promotes whatever
/// was directly inside it -- sub-folders and documents alike -- to ITS OWN parent rather than orphaning or
/// deleting them (<see cref="DocumentStore.DeleteFolder"/>), the same "nothing inside is ever destroyed" rule
/// documents themselves already get.</summary>
public sealed class FolderRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentFolderId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DocumentRecord))]
[JsonSerializable(typeof(List<FolderRecord>))]
internal sealed partial class DocumentJsonContext : JsonSerializerContext;

/// <summary>A page removed with <see cref="DocumentStore.TrashPage"/>: enough to put it back where it was.</summary>
public sealed record DeletedPage(string DocId, PageRecord Page, int Index);
