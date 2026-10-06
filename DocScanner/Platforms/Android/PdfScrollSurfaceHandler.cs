using DocScanner.Services;
using Microsoft.Maui.Handlers;

namespace DocScanner.Views;

/// <summary>Maps <see cref="PdfScrollSurface"/> to the native <see cref="PdfScrollView"/>, so MAUI's own
/// measure/arrange gives it a real size (see <see cref="PdfScrollSurface"/>'s doc comment for why that matters:
/// without a proper Handler, the page it is used on opened to a plain black screen). No properties to map --
/// everything about the PDF being shown is driven straight from code-behind (<c>PdfViewerPage.xaml.cs</c>) through
/// the platform view itself, not through bindable properties here.</summary>
internal sealed class PdfScrollSurfaceHandler : ViewHandler<PdfScrollSurface, PdfScrollView>
{
	public static readonly IPropertyMapper<PdfScrollSurface, PdfScrollSurfaceHandler> Mapper =
		new PropertyMapper<PdfScrollSurface, PdfScrollSurfaceHandler>(ViewHandler.ViewMapper);

	public PdfScrollSurfaceHandler() : base(Mapper)
	{
	}

	protected override PdfScrollView CreatePlatformView() => new(Context);
}
