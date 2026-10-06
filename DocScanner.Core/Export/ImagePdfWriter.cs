using System.Globalization;
using System.Text;
using ImageCoreService;

namespace DocScanner.Core;

/// <summary>One page of an image-only PDF.</summary>
/// <param name="ImageFile">A baseline JPEG (embedded byte for byte) or a PNG written by <see cref="PngWriter"/>
/// (its compressed data is embedded as-is).</param>
/// <param name="WidthPt">Page size in PDF points (1/72 inch).</param>
public sealed record PdfPageSource(string ImageFile, double WidthPt, double HeightPt)
{
    /// <summary>Where the image goes on the page, in points from the bottom-left corner; null = the whole page.</summary>
    public (double X, double Y, double Width, double Height)? ImageRect { get; init; }
}

/// <summary>
/// Writes a PDF made of one full-page image per page, streaming straight to the output: only one
/// page file is in memory at a time and no image is ever decoded. JPEG pages go in with
/// <c>/DCTDecode</c>; PNG pages with <c>/FlateDecode</c> plus the PNG predictor, which is exactly how
/// PNG stores its rows. Self-contained (no PDF library) so it behaves the same on the phone, under
/// trimming, and in unit tests.
/// </summary>
public static class ImagePdfWriter
{
    /// <summary>A4 in points.</summary>
    public const double A4ShortPt = 595.276, A4LongPt = 841.890;

    public static async Task WriteAsync(Stream output, IReadOnlyList<PdfPageSource> pages, string title,
        IProgress<int>? pageWritten = null, CancellationToken ct = default)
    {
        if (pages.Count == 0) throw new ArgumentException("A PDF needs at least one page.", nameof(pages));
        var w = new CountingWriter(output);
        var offsets = new List<long> { 0 }; // object 0 is the free-list head

        // Object numbers: 1 catalog, 2 page tree, 3 info, then (page, contents, image) per page.
        int PageObj(int i) => 4 + 3 * i;

        await w.WriteAsync("%PDF-1.7\n%âãÏÓ\n", ct);

        async Task Obj(int number, string body)
        {
            Record(offsets, number, w.Position);
            await w.WriteAsync($"{number} 0 obj\n{body}\nendobj\n", ct);
        }

        await Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        string kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{PageObj(i)} 0 R"));
        await Obj(2, $"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        string now = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        await Obj(3, $"<< /Title {TextString(title)} /Producer (Doc Scanner) /Creator (Doc Scanner) /CreationDate (D:{now}) >>");

        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            PdfPageSource page = pages[i];
            int pageObj = PageObj(i), contentsObj = pageObj + 1, imageObj = pageObj + 2;
            string wPt = Num(page.WidthPt), hPt = Num(page.HeightPt);

            await Obj(pageObj,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {wPt} {hPt}] " +
                $"/Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentsObj} 0 R >>");

            var r = page.ImageRect ?? (0, 0, page.WidthPt, page.HeightPt);
            byte[] content = Encoding.ASCII.GetBytes($"q {Num(r.Width)} 0 0 {Num(r.Height)} {Num(r.X)} {Num(r.Y)} cm /Im0 Do Q\n");
            Record(offsets, contentsObj, w.Position);
            await w.WriteAsync($"{contentsObj} 0 obj\n<< /Length {content.Length} >>\nstream\n", ct);
            await w.WriteAsync(content, ct);
            await w.WriteAsync("\nendstream\nendobj\n", ct);

            byte[] file = await File.ReadAllBytesAsync(page.ImageFile, ct);
            (string dict, byte[] data) = ImageObject(file, page.ImageFile);
            Record(offsets, imageObj, w.Position);
            await w.WriteAsync($"{imageObj} 0 obj\n<< /Type /XObject /Subtype /Image {dict} /Length {data.Length} >>\nstream\n", ct);
            await w.WriteAsync(data, ct);
            await w.WriteAsync("\nendstream\nendobj\n", ct);
            pageWritten?.Report(i + 1);
        }

        long xref = w.Position;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f\r\n");
        for (int n = 1; n < offsets.Count; n++) sb.Append($"{offsets[n]:D10} 00000 n\r\n");
        string id = Guid.NewGuid().ToString("N");
        sb.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R /Info 3 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        await w.WriteAsync(sb.ToString(), ct);
        await output.FlushAsync(ct);
    }

    /// <summary>The image dictionary entries and the stream data for one page file.</summary>
    private static (string Dict, byte[] Data) ImageObject(byte[] file, string path)
    {
        if (file.Length > 3 && file[0] == 0xFF && file[1] == 0xD8)
        {
            (int width, int height, int components) = JpegInfo.Read(file);
            string cs = components switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" };
            string decode = components == 4 ? " /Decode [1 0 1 0 1 0 1 0]" : ""; // Adobe CMYK JPEGs are inverted
            return ($"/Width {width} /Height {height} /ColorSpace {cs} /BitsPerComponent 8 /Filter /DCTDecode{decode}", file);
        }

        PngReader.PngData png = PngReader.Read(file);
        if (png.ColorType != 0 || png.BitDepth is not (1 or 8))
            throw new InvalidDataException($"Unsupported PNG (color type {png.ColorType}, {png.BitDepth} bit): {path}");
        return ($"/Width {png.Width} /Height {png.Height} /ColorSpace /DeviceGray /BitsPerComponent {png.BitDepth} " +
                $"/Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors 1 /BitsPerComponent {png.BitDepth} /Columns {png.Width} >>",
                png.ZlibData);
    }

    private static void Record(List<long> offsets, int number, long position)
    {
        while (offsets.Count <= number) offsets.Add(0);
        offsets[number] = position;
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>PDF text string in UTF-16BE (hex), so Vietnamese titles survive.</summary>
    private static string TextString(string s)
    {
        var sb = new StringBuilder("<FEFF");
        foreach (char c in s) sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        return sb.Append('>').ToString();
    }

    /// <summary>Tracks the byte offset for the cross-reference table.</summary>
    private sealed class CountingWriter(Stream s)
    {
        public long Position { get; private set; }

        public Task WriteAsync(string text, CancellationToken ct) => WriteAsync(Encoding.Latin1.GetBytes(text), ct);

        public async Task WriteAsync(byte[] data, CancellationToken ct)
        {
            await s.WriteAsync(data, ct);
            Position += data.Length;
        }
    }
}

/// <summary>Size and component count from a JPEG's frame header.</summary>
public static class JpegInfo
{
    public static (int Width, int Height, int Components) Read(byte[] jpeg)
    {
        int i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            byte marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            if (marker is 0xD9 or 0xDA) break;
            int len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            bool sof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (sof && i + 9 < jpeg.Length)
                return ((jpeg[i + 7] << 8) | jpeg[i + 8], (jpeg[i + 5] << 8) | jpeg[i + 6], jpeg[i + 9]);
            i += 2 + len;
        }
        throw new InvalidDataException("JPEG has no frame header.");
    }
}
