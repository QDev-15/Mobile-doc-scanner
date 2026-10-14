using System.Text.Json;
using System.Text.Json.Serialization;
using ImageCoreService;

namespace DocScanner.Core.Signatures;

/// <summary>A saved signature: its ink color (0xRRGGBB) and mask size. <paramref name="IsPicture"/>: a colour picture signature (its own
/// colours, kept as an RGBA PNG) rather than a mask in one ink colour; <paramref name="Color"/> is then unused. Missing in older index
/// files = false.</summary>
public sealed record SignatureInfo(string Id, uint Color, int Width, int Height, DateTime CreatedUtc, bool IsPicture = false);

/// <summary>
/// The user's saved signatures, to be placed on any page: <c>{folder}/{id}.png</c> (8-bit gray ink mask, 0 = paper,
/// 255 = ink; for a colour picture signature an 8-bit RGBA PNG instead) plus <c>index.json</c> (newest first). A signature that pages still use can be deleted from the list; the
/// pages keep a reference and simply draw nothing for it. Thread-safe.
/// </summary>
public sealed class SignatureLibrary(string folder)
{
    private readonly object _lock = new();
    private List<SignatureInfo>? _index;
    private readonly Dictionary<string, SignatureInkImage> _masks = [];

    public string Folder { get; } = folder;

    public string PngPath(string id) => Path.Combine(Folder, id + ".png");

    /// <summary>The signature as a picture (its ink color, transparent paper) for the screens; made when missing.</summary>
    public string ViewPath(string id)
    {
        // A colour picture signature is already an RGBA picture (its colours, transparent where there is no ink): show that as it is.
        if (Get(id) is { IsPicture: true }) return PngPath(id);
        string path = Path.Combine(Folder, id + "_view.png");
        if (!File.Exists(path) && Ink(id) is { } ink) File.WriteAllBytes(path, ViewPng(ink));
        return path;
    }

    /// <summary>RGBA PNG of an ink mask: the ink color everywhere, the mask as alpha.</summary>
    public static byte[] ViewPng(SignatureInkImage ink)
    {
        GrayImage m = ink.Mask;
        var rgba = new byte[m.Width * m.Height * 4];
        byte r = (byte)(ink.Color >> 16), g = (byte)(ink.Color >> 8), b = (byte)ink.Color;
        for (int i = 0; i < m.Data.Length; i++)
        {
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = m.Data[i];
        }
        return PngWriter.EncodeRgba(m.Width, m.Height, rgba);
    }

    public IReadOnlyList<SignatureInfo> List()
    {
        lock (_lock) return [.. Index()];
    }

    public SignatureInfo? Get(string id)
    {
        lock (_lock) return Index().FirstOrDefault(s => s.Id == id);
    }

    public SignatureInfo Add(GrayImage mask, uint color)
    {
        var info = new SignatureInfo(Guid.NewGuid().ToString("N"), color & 0xFFFFFF, mask.Width, mask.Height, DateTime.UtcNow);
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(PngPath(info.Id), PngWriter.EncodeGray8(mask));
        lock (_lock)
        {
            Index().Insert(0, info);
            _masks[info.Id] = new SignatureInkImage(mask, info.Color);
            SaveIndex();
        }
        return info;
    }

    /// <summary>Saves a COLOUR picture signature: its own per-pixel colours (<paramref name="colors"/>) with <paramref name="alpha"/> as
    /// transparency (255 everywhere = the picture as it is; lower where the background was cut out). Stored as one RGBA PNG.</summary>
    public SignatureInfo AddPicture(RgbImage colors, GrayImage alpha)
    {
        if (colors.Width != alpha.Width || colors.Height != alpha.Height) throw new ArgumentException("Colours and alpha must have the same size.");
        var info = new SignatureInfo(Guid.NewGuid().ToString("N"), 0, colors.Width, colors.Height, DateTime.UtcNow, IsPicture: true);
        var rgba = new byte[colors.Width * colors.Height * 4];
        for (int i = 0; i < alpha.Data.Length; i++)
        {
            rgba[i * 4] = colors.Data[i * 3];
            rgba[i * 4 + 1] = colors.Data[i * 3 + 1];
            rgba[i * 4 + 2] = colors.Data[i * 3 + 2];
            rgba[i * 4 + 3] = alpha.Data[i];
        }
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(PngPath(info.Id), PngWriter.EncodeRgba(colors.Width, colors.Height, rgba));
        lock (_lock)
        {
            Index().Insert(0, info);
            _masks[info.Id] = new SignatureInkImage(alpha, 0, colors);
            SaveIndex();
        }
        return info;
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (Index().RemoveAll(s => s.Id == id) == 0) return false;
            _masks.Remove(id);
            SaveIndex();
        }
        try
        {
            File.Delete(PngPath(id));
            File.Delete(Path.Combine(Folder, id + "_view.png"));
        }
        catch (IOException) { }
        return true;
    }

    /// <summary>The ink of a signature (kept in memory once read), or null when it no longer exists.</summary>
    public SignatureInkImage? Ink(string id)
    {
        lock (_lock)
        {
            if (_masks.TryGetValue(id, out SignatureInkImage? known)) return known;
            SignatureInfo? info = Index().FirstOrDefault(s => s.Id == id);
            string path = PngPath(id);
            if (info == null || !File.Exists(path)) return null;
            SignatureInkImage ink;
            if (info.IsPicture)
            {
                (RgbImage rgb, GrayImage alpha) = PngReader.DecodeRgba8(File.ReadAllBytes(path));
                ink = new SignatureInkImage(alpha, 0, rgb);
            }
            else
            {
                ink = new SignatureInkImage(PngReader.DecodeGray8(File.ReadAllBytes(path)), info.Color);
            }
            _masks[id] = ink;
            return ink;
        }
    }

    private List<SignatureInfo> Index()
    {
        if (_index != null) return _index;
        string path = Path.Combine(Folder, "index.json");
        try
        {
            _index = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), SignatureJsonContext.Default.ListSignatureInfo) ?? []
                : [];
        }
        catch (JsonException)
        {
            _index = [];
        }
        return _index;
    }

    private void SaveIndex()
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, "index.json"), tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Index(), SignatureJsonContext.Default.ListSignatureInfo));
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<SignatureInfo>))]
internal sealed partial class SignatureJsonContext : JsonSerializerContext;
