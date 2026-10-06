namespace DocScanner.Views;

/// <summary>Cross-platform placeholder for <c>PdfScrollView</c> (Platforms/Android/PdfScrollView.cs): a MAUI View
/// with a custom Handler, so the native canvas gets normal MAUI measure/arrange -- the only way this Grid-hosted
/// custom view ends up with a real size.
///
/// An earlier version instead added PdfScrollView as a manual child of a plain ContentView's native container
/// (the "grab the native view" trick ZoomImageHost uses to take over a single ImageView). For an EXTRA child a
/// ContentView's own native container never measures or lays out on its own, that left it permanently at 0x0 --
/// confirmed on-device (2026-10-05): the PDF viewer opened to a plain black screen, "Trang 1/10" in the floating
/// pill (that part only needs PdfPages.Count, no layout) but nothing ever drawn, and logging showed OnSizeChanged
/// never firing at all. A real Handler is what ZoomImageHost's trick is a shortcut around for a single picture;
/// this view needs the genuine thing since it is the whole page's content, not one picture inside a page.</summary>
public sealed class PdfScrollSurface : View;
