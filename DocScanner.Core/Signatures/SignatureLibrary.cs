using System.Text.Json;
using System.Text.Json.Serialization;
using ImageCoreService;

namespace DocScanner.Core.Signatures;

/// <summary>A saved signature: its ink color (0xRRGGBB) and mask size.</summary>
public sealed record SignatureInfo(string Id, uint Color, int Width, int Height, DateTime CreatedUtc);

/// <summary>
/// The user's saved signatures, to be placed on any page: <c>{folder}/{id}.png</c> (8-bit gray ink mask, 0 = paper,
/// 255 = ink) plus <c>index.json</c> (newest first). A signature that pages still use can be deleted from the list; the
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
            var ink = new SignatureInkImage(PngReader.DecodeGray8(File.ReadAllBytes(path)), info.Color);
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
