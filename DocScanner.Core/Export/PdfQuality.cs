namespace DocScanner.Core;

/// <summary>
/// How color / gray pages go into an exported PDF. The pages kept in the app stay at full quality
/// (up to A4 at 300 DPI, JPEG 94: good for viewing and re-editing); at export they are shrunk to
/// <see cref="Dpi"/> and re-encoded at <see cref="JpegQuality"/>.
///
/// Black-and-white pages are saved anti-aliased (8-bit gray PNG, soft stroke edges): a hard 1-bit edge showed a
/// visible pixel staircase once a page was zoomed past its own resolution ("chữ bị vỡ khi phóng to", owner's report
/// 2026-09-28c) -- no amount of resolution fixes that, only keeping the soft edge does. <see cref="High"/> keeps that
/// page exactly as saved (full resolution, ~700 KB for an A4 page of text). <see cref="Medium"/> shrinks it first, to
/// <see cref="GrayDpi"/>, staying anti-aliased (~350-400 KB): still soft at any normal zoom, well short of Cao's size.
/// <see cref="Small"/> has no <see cref="GrayDpi"/> and keeps the old 1-bit embedding instead (~150-200 KB): it is the
/// tier for a size limit (Zalo, email), where losing the smooth edge is the accepted trade.
/// </summary>
public sealed record PdfQuality(string Key, string Label, int Dpi, int JpegQuality, int? GrayDpi = null)
{
    /// <summary>Email / chat apps: about 100-200 KB per color page; black-and-white pages stay 1-bit (~150-200 KB), the
    /// smallest a page of text gets.</summary>
    public static readonly PdfQuality Small = new("small", "Nhỏ · gửi Zalo, email (150 DPI)", 150, 60);

    /// <summary>The default: sharp on screen and fine to print, about 150-300 KB per cleaned color page;
    /// black-and-white pages anti-aliased at 150 DPI (~350-400 KB: bigger than the old 1-bit embedding, but the
    /// text keeps its smooth, printed look at any normal zoom).</summary>
    public static readonly PdfQuality Medium = new("medium", "Vừa · khuyên dùng (200 DPI)", 200, 72, GrayDpi: 150);

    /// <summary>Printing / archiving: full resolution, anti-aliased. <see cref="GrayDpi"/> equals <see cref="Dpi"/>
    /// (the black-and-white page is already at that resolution): <see cref="PdfExportService"/> keeps it exactly as
    /// saved rather than decoding and re-encoding it for no change.</summary>
    public static readonly PdfQuality High = new("high", "Cao · in ấn (300 DPI)", 300, 90, GrayDpi: 300);

    public static IReadOnlyList<PdfQuality> All { get; } = [Small, Medium, High];

    public static PdfQuality FromKey(string? key) => All.FirstOrDefault(q => q.Key == key) ?? Medium;

    /// <summary>Long edge in pixels for a color page at this resolution (the long side of an A4 sheet, 11.69 in).</summary>
    public int LongEdgePx => (int)Math.Round(11.69 * Dpi);

    /// <summary>Long edge in pixels a black-and-white page is shrunk to, staying anti-aliased; null (<see cref="Small"/>)
    /// keeps the old, smaller 1-bit embedding instead.</summary>
    public int? GrayLongEdgePx => GrayDpi is { } dpi ? (int)Math.Round(11.69 * dpi) : null;
}
