using ImageCoreService;

namespace DocScanner.Core;

/// <summary>Size and EXIF orientation read from an original photo.</summary>
public sealed record ImageInfo(int RawWidth, int RawHeight, int ExifOrientation);

/// <summary>Platform image decoding (Android: BitmapFactory + ExifInterface). Every method must
/// decode with a power-of-two sample size, so a 48 MP photo never needs a full-size bitmap in RAM,
/// and must never modify the original.</summary>
public interface IImageService
{
    /// <summary>The fast first stage: reads size and EXIF orientation and writes a small upright
    /// (orientation applied) JPEG whose long edge is at most <paramref name="thumbEdge"/>. Cheap
    /// enough (a heavily sub-sampled decode) that every page of a batch can show a thumbnail within
    /// moments of being added. <paramref name="userRotationDegrees"/> is the page's extra clockwise rotation,
    /// applied on top of the file's own EXIF orientation.</summary>
    Task<ImageInfo> CreateThumbAsync(string originalPath, string thumbPath, int thumbEdge, int userRotationDegrees, CancellationToken ct);

    /// <summary>The screen proxy: an upright JPEG with the long edge at most <paramref name="proxyEdge"/>.
    /// <paramref name="orientation"/> is the page's effective orientation (EXIF combined with the user rotation).</summary>
    Task CreateProxyAsync(string originalPath, string proxyPath, int proxyEdge, int orientation, CancellationToken ct);

    /// <summary>Decodes one rectangle of the ORIGINAL (in the stored, unrotated pixel grid) shrunk by the
    /// power-of-two <paramref name="sample"/>. Only that rectangle may be decoded (a 48 MP photo cannot be
    /// held whole); the result is about <c>width / sample</c> by <c>height / sample</c> pixels.</summary>
    Task<RgbImage> LoadRegionAsync(string originalPath, int x, int y, int width, int height, int sample, CancellationToken ct);

    /// <summary>Encodes an image as JPEG.</summary>
    Task SaveJpegAsync(RgbImage image, string path, int quality, CancellationToken ct);

    /// <summary>Decodes an image file for analysis, shrunk so its long edge is at most
    /// <paramref name="maxEdge"/> (orientation is NOT applied: pass an already upright file
    /// such as the proxy).</summary>
    Task<RgbImage> LoadRgbAsync(string path, int maxEdge, CancellationToken ct);
}
