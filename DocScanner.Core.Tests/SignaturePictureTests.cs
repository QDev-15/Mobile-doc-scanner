using DocScanner.Core.Signatures;
using ImageCoreService;

namespace DocScanner.Core.Tests;

/// <summary>Colour picture signatures: a picked image kept in its own colours (as it is, or with the paper cut out).</summary>
public class SignaturePictureTests
{
    /// <summary>White paper (240) with a thick blue pen stroke (20, 40, 200) across the middle and a red dot.</summary>
    private static RgbImage PaperWithBlueSignature(int w = 200, int h = 100)
    {
        var img = new RgbImage(w, h);
        for (int i = 0; i < w * h; i++) { img.Data[i * 3] = 240; img.Data[i * 3 + 1] = 240; img.Data[i * 3 + 2] = 240; }
        for (int y = 45; y < 55; y++)
        {
            for (int x = 40; x < 160; x++)
            {
                int o = (y * w + x) * 3;
                img.Data[o] = 20; img.Data[o + 1] = 40; img.Data[o + 2] = 200;
            }
        }
        return img;
    }

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "sigpic_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void An_RGBA_PNG_decodes_back_to_its_colours_and_its_alpha()
    {
        var rgba = new byte[3 * 2 * 4];
        for (int i = 0; i < 6; i++)
        {
            rgba[i * 4] = (byte)(i * 10);
            rgba[i * 4 + 1] = (byte)(i * 20);
            rgba[i * 4 + 2] = (byte)(255 - i * 10);
            rgba[i * 4 + 3] = (byte)(i * 50);
        }
        (RgbImage rgb, GrayImage alpha) = PngReader.DecodeRgba8(PngWriter.EncodeRgba(3, 2, rgba));
        Assert.Equal((3, 2), (rgb.Width, rgb.Height));
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(rgba[i * 4], rgb.Data[i * 3]);
            Assert.Equal(rgba[i * 4 + 1], rgb.Data[i * 3 + 1]);
            Assert.Equal(rgba[i * 4 + 2], rgb.Data[i * 3 + 2]);
            Assert.Equal(rgba[i * 4 + 3], alpha.Data[i]);
        }
    }

    [Fact]
    public void Keep_original_is_the_picture_untouched_and_fully_opaque()
    {
        RgbImage src = PaperWithBlueSignature();
        SignatureImageImport.Picture p = SignatureImageImport.ToPicture(src, SignatureImageMode.KeepOriginal);
        Assert.Same(src, p.Colors);
        Assert.All(p.Alpha.Data, a => Assert.Equal(255, a));
        Assert.Throws<ArgumentException>(() => SignatureImageImport.ToPicture(src, SignatureImageMode.InkColor));
    }

    [Fact]
    public void Remove_background_cuts_the_paper_out_keeps_the_ink_colour_and_crops_to_the_ink()
    {
        RgbImage src = PaperWithBlueSignature();
        SignatureImageImport.Picture p = SignatureImageImport.ToPicture(src, SignatureImageMode.RemoveBackground);

        Assert.True(p.Colors.Width < src.Width && p.Colors.Height < src.Height, "cropped to the ink");
        Assert.True(p.Colors.Width >= 120 && p.Colors.Height >= 10, "still holds the whole stroke");
        Assert.Equal(p.Colors.Width * p.Colors.Height, p.Alpha.Data.Length);

        // The middle of the stroke: opaque and still blue (not gray, not washed out).
        int cx = p.Colors.Width / 2, cy = p.Colors.Height / 2, o = cy * p.Colors.Width + cx;
        Assert.Equal(255, p.Alpha.Data[o]);
        Assert.True(p.Colors.Data[o * 3 + 2] > 150 && p.Colors.Data[o * 3] < 80, "blue ink stays blue");
        // A corner of the crop is paper: transparent.
        Assert.Equal(0, p.Alpha.Data[0]);
    }

    [Fact]
    public void A_colour_picture_signature_is_saved_reloaded_and_listed_as_a_picture()
    {
        string folder = TempFolder();
        try
        {
            var lib = new SignatureLibrary(folder);
            var colors = new RgbImage(8, 4);
            for (int i = 0; i < colors.Data.Length; i++) colors.Data[i] = (byte)(i * 7 + 3); // distinct bytes: any mix-up shows
            var alpha = new GrayImage(8, 4);
            Array.Fill(alpha.Data, (byte)200);
            SignatureInfo info = lib.AddPicture(colors, alpha);
            Assert.True(info.IsPicture);

            var reopened = new SignatureLibrary(folder); // a fresh library: nothing cached, everything read from disk
            Assert.True(reopened.Get(info.Id)!.IsPicture);
            SignatureInkImage ink = reopened.Ink(info.Id)!;
            Assert.NotNull(ink.Picture);
            Assert.Equal(colors.Data, ink.Picture!.Data);
            Assert.Equal(alpha.Data, ink.Mask.Data);
            Assert.Equal(reopened.PngPath(info.Id), reopened.ViewPath(info.Id)); // shown as the RGBA picture itself
            Assert.Throws<ArgumentException>(() => lib.AddPicture(colors, new GrayImage(3, 3)));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_drawn_signature_is_not_a_picture_and_old_index_files_still_load()
    {
        string folder = TempFolder();
        try
        {
            var lib = new SignatureLibrary(folder);
            var mask = new GrayImage(4, 2);
            Array.Fill(mask.Data, (byte)255);
            SignatureInfo info = lib.Add(mask, 0x1A3A9E);
            Assert.False(info.IsPicture);
            Assert.Null(lib.Ink(info.Id)!.Picture);

            // An index written before IsPicture existed has no such field.
            string indexPath = Path.Combine(folder, "index.json");
            File.WriteAllText(indexPath, "[{\"id\":\"" + info.Id + "\",\"color\":1719966,\"width\":4,\"height\":2,\"createdUtc\":\"2026-01-01T00:00:00Z\"}]");
            Assert.False(new SignatureLibrary(folder).Get(info.Id)!.IsPicture);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    /// <summary>20 x 4 picture, opaque: the left half red, the right half blue.</summary>
    private static SignatureInkImage RedAndBluePicture()
    {
        var colors = new RgbImage(20, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                int o = (y * 20 + x) * 3;
                if (x < 10) colors.Data[o] = 255; else colors.Data[o + 2] = 255;
            }
        }
        var alpha = new GrayImage(20, 4);
        Array.Fill(alpha.Data, (byte)255);
        return new SignatureInkImage(alpha, 0, colors);
    }

    [Fact]
    public void A_colour_picture_keeps_its_own_colours_on_a_colour_page()
    {
        var page = new RgbImage(100, 100);
        Array.Fill(page.Data, (byte)255);
        // Half a page wide (50 px), centred: the halves are centred at about x = 37 and x = 62.
        Stamper.Apply(page, [new PageStamp("p", 0.5, 0.5, 0.5)], _ => RedAndBluePicture());

        int Pixel(int x, int y) { int o = (y * 100 + x) * 3; return page.Data[o] << 16 | page.Data[o + 1] << 8 | page.Data[o + 2]; }
        int left = Pixel(37, 50), right = Pixel(62, 50);
        Assert.True((left >> 16 & 0xFF) > 200 && (left & 0xFF) < 60, "left side red");
        Assert.True((right & 0xFF) > 200 && (right >> 16 & 0xFF) < 60, "right side blue");
        Assert.Equal(0xFFFFFF, Pixel(5, 5)); // outside the stamp: the page is untouched
    }

    [Fact]
    public void A_colour_picture_on_a_gray_page_uses_its_gray_level_and_on_a_one_bit_page_only_black_or_white()
    {
        var gray = new GrayImage(100, 100);
        Array.Fill(gray.Data, (byte)255);
        Stamper.Apply(gray, [new PageStamp("p", 0.5, 0.5, 0.5)], _ => RedAndBluePicture(), bilevel: false);
        Assert.InRange(gray.Data[50 * 100 + 37], 60, 90);   // red's luma, not black and not white
        Assert.Equal(255, gray.Data[5 * 100 + 5]);

        var bilevel = new GrayImage(100, 100);
        Array.Fill(bilevel.Data, (byte)255);
        Stamper.Apply(bilevel, [new PageStamp("p", 0.5, 0.5, 0.5)], _ => RedAndBluePicture(), bilevel: true);
        Assert.All(bilevel.Data, v => Assert.True(v is 0 or 255));
        Assert.Equal(0, bilevel.Data[50 * 100 + 37]);        // red's luma < 128 -> black
    }

    [Fact]
    public void A_transparent_part_of_a_picture_does_not_bleed_its_colour_into_the_edge()
    {
        // Left pixel opaque red, right pixel fully transparent but "black": the edge must stay red, not turn dark.
        var colors = new RgbImage(2, 1);
        colors.Data[0] = 255;
        var alpha = new GrayImage(2, 1);
        alpha.Data[0] = 255;
        var page = new RgbImage(100, 100);
        Array.Fill(page.Data, (byte)255);
        Stamper.Apply(page, [new PageStamp("p", 0.5, 0.5, 0.5)], _ => new SignatureInkImage(alpha, 0, colors));
        int o = (50 * 100 + 48) * 3; // near the red/transparent boundary, still inside the soft edge
        Assert.True(page.Data[o] > 200 && page.Data[o + 1] > 60, "stays red-ish over white, not black");
    }
}
