using DocScanner.Core.Signatures;
using ImageCoreService;

namespace DocScanner.Core.Tests;

public class SignatureTests
{
    private static GrayImage Solid(int w, int h)
    {
        var m = new GrayImage(w, h);
        Array.Fill(m.Data, (byte)255);
        return m;
    }

    [Fact]
    public void A_stroke_becomes_an_anti_aliased_mask_cropped_to_the_ink()
    {
        GrayImage mask = SignatureInk.Rasterize([[new PointD(100, 50), new PointD(300, 50)]], penWidth: 6, maxEdge: 400)!;
        Assert.Equal(400, Math.Max(mask.Width, mask.Height));   // scaled to the requested size
        Assert.True(mask.Width > mask.Height * 10);             // a thin horizontal line, margins included
        int midRow = mask.Height / 2;
        Assert.Equal(255, mask.Data[midRow * mask.Width + mask.Width / 2]);
        Assert.Equal(0, mask.Data[mask.Width / 2]);                         // top row: paper
        Assert.Contains(mask.Data, v => v is > 0 and < 255);                  // soft rim
        Assert.Null(SignatureInk.Rasterize([], 6));
    }

    [Fact]
    public void Stamps_turn_with_the_page_and_four_turns_are_nothing()
    {
        var s = new PageStamp("x", 0.2, 0.1, 0.3);
        Assert.Equal(new PageStamp("x", 0.9, 0.2, 0.3, 1), s.Rotate(1));
        PageStamp full = s.Rotate(1).Rotate(1).Rotate(1).Rotate(1);
        Assert.Equal((0.2, 0.1, 0), (Math.Round(full.CenterX, 9), Math.Round(full.CenterY, 9), full.Turns));
        Assert.Equal(s.Rotate(3), s.Rotate(-1));
    }

    [Fact]
    public void A_signature_is_drawn_in_its_ink_color_on_a_color_page_and_nowhere_else()
    {
        var page = new RgbImage(400, 600);
        Array.Fill(page.Data, (byte)250);
        var ink = new SignatureInkImage(Solid(100, 50), 0x1A3A9E);
        var stamp = new PageStamp("sig", 0.5, 0.5, 0.5); // 200 x 100 px around the center
        Stamper.Apply(page, [stamp], id => id == "sig" ? ink : null);

        int Center(int x, int y) => (y * 400 + x) * 3;
        Assert.Equal((0x1A, 0x3A, 0x9E), (page.Data[Center(200, 300)], page.Data[Center(200, 300) + 1], page.Data[Center(200, 300) + 2]));
        Assert.Equal(250, page.Data[Center(200, 200)]);  // above the 100 px tall footprint
        Assert.Equal(250, page.Data[Center(50, 300)]);   // left of the 200 px wide footprint
    }

    [Fact]
    public void A_turned_stamp_draws_the_signature_turned()
    {
        // Ink only in the left half of the signature: turned a quarter clockwise, that half ends up on top.
        var mask = new GrayImage(100, 50);
        for (int y = 0; y < 50; y++) for (int x = 0; x < 50; x++) mask.Data[y * 100 + x] = 255;
        var page = new GrayImage(400, 400);
        Array.Fill(page.Data, (byte)255);
        Stamper.Apply(page, [new PageStamp("s", 0.5, 0.5, 0.5, Turns: 1)], _ => new SignatureInkImage(mask, 0), bilevel: false);
        // Footprint: 100 wide x 200 tall around (200, 200).
        Assert.Equal(0, page.Data[150 * 400 + 200]);   // upper half: ink
        Assert.Equal(255, page.Data[250 * 400 + 200]); // lower half: paper
        Assert.Equal(255, page.Data[200 * 400 + 260]); // outside the 100 px wide footprint
    }

    [Fact]
    public void On_a_one_bit_page_the_ink_is_black()
    {
        var page = new GrayImage(200, 200);
        Array.Fill(page.Data, (byte)255);
        Stamper.Apply(page, [new PageStamp("s", 0.5, 0.5, 0.4)], _ => new SignatureInkImage(Solid(10, 10), 0x1A3A9E), bilevel: true);
        Assert.All(page.Data, v => Assert.True(v is 0 or 255));
        Assert.Equal(0, page.Data[100 * 200 + 100]);
    }

    [Fact]
    public void A_picked_photo_becomes_an_ink_mask_dark_pixels_become_ink()
    {
        var page = new RgbImage(100, 60);
        Array.Fill(page.Data, (byte)240); // light paper
        for (int y = 20; y < 40; y++)     // a dark "ink" block in the middle
            for (int x = 30; x < 70; x++)
            {
                int i = (y * 100 + x) * 3;
                page.Data[i] = page.Data[i + 1] = page.Data[i + 2] = 20;
            }

        GrayImage mask = SignatureImageImport.ToMask(page);
        Assert.Equal((100, 60), (mask.Width, mask.Height));
        Assert.True(mask.Data[30 * 100 + 50] > 200); // inside the dark block: ink
        Assert.True(mask.Data[5 * 100 + 5] < 20);    // paper corner: no ink
    }

    [Fact]
    public void The_library_keeps_signatures_across_restarts()
    {
        using var root = new TempRoot();
        var lib = new SignatureLibrary(root.Path);
        SignatureInfo a = lib.Add(Solid(30, 10), 0x000000);
        SignatureInfo b = lib.Add(Solid(20, 10), 0x1A3A9E);

        var again = new SignatureLibrary(root.Path);
        Assert.Equal([b.Id, a.Id], again.List().Select(s => s.Id));      // newest first
        Assert.Equal((20, 10, 0x1A3A9Eu), (again.Ink(b.Id)!.Mask.Width, again.Ink(b.Id)!.Mask.Height, again.Ink(b.Id)!.Color));
        Assert.True(again.Delete(a.Id));
        Assert.Null(again.Ink(a.Id));
        Assert.Single(new SignatureLibrary(root.Path).List());
    }

    [Fact]
    public async Task A_signed_page_is_rendered_with_the_signature_and_removing_it_renders_again()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(Path.Combine(root.Path, "docs"));
        var images = new FakeImageService();
        var lib = new SignatureLibrary(Path.Combine(root.Path, "sig"));
        var render = new CropRenderService(store, images, lib);
        var queue = new PageIngestQueue(store, images, null, 1, render);
        var edit = new PageEditService(store, queue, null);
        DocumentRecord doc = store.Create("Hợp đồng");
        await new ImportService(store, queue).ImportAsync(doc, [new ImportSource("a.jpg", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))]);
        await queue.WaitIdleAsync();
        string pageId = store.Pages(doc.Id)[0].Id;
        edit.SetFilter(doc.Id, pageId, PageColorMode.BlackWhite);

        SignatureInfo sig = lib.Add(Solid(40, 20), 0x000000);
        var stamp = new PageStamp(sig.Id, 0.5, 0.5, 0.4);
        Assert.True(edit.SetStamps(doc.Id, pageId, [stamp]));
        Assert.True(store.Pages(doc.Id)[0].NeedsRender);

        await render.RenderAsync(doc.Id, pageId);
        PageRecord p = store.Pages(doc.Id)[0];
        Assert.False(p.NeedsRender);
        Assert.Equal([stamp], p.CroppedStamps!);
        GrayImage page = PngReader.DecodeGray8(File.ReadAllBytes(store.CroppedPath(doc.Id, p)));
        Assert.Equal(0, page.Data[page.Height / 2 * page.Width + page.Width / 2]); // the signature, in the middle

        edit.RotateOutput(doc.Id, pageId, 90);
        Assert.Equal(stamp.Rotate(1), store.Pages(doc.Id)[0].Stamps!.Single());

        Assert.True(edit.SetStamps(doc.Id, pageId, null));
        Assert.True(store.Pages(doc.Id)[0].NeedsRender);
        Assert.Null(store.Pages(doc.Id)[0].Stamps);
    }
}
