using DocScanner.Views;

namespace DocScanner;

public partial class AppShell : Shell
{
	public static class Routes
	{
		public const string Document = "document";
		public const string Crop = "crop";
		public const string Result = "result";
		public const string Exports = "exports";
		public const string Viewer = "viewer";
		public const string PdfViewer = "pdfviewer";
		public const string Signature = "signature";
		public const string Folder = "folder";
		public const string Settings = "settings";
		public const string About = "about";
	}

	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute(Routes.Document, typeof(DocumentPage));
		Routing.RegisterRoute(Routes.Crop, typeof(CropPage));
		Routing.RegisterRoute(Routes.Result, typeof(ResultPage));
		Routing.RegisterRoute(Routes.Exports, typeof(ExportsPage));
		Routing.RegisterRoute(Routes.Viewer, typeof(ViewerPage));
		Routing.RegisterRoute(Routes.PdfViewer, typeof(PdfViewerPage));
		Routing.RegisterRoute(Routes.Signature, typeof(SignaturePage));
		Routing.RegisterRoute(Routes.Folder, typeof(HomePage));
		Routing.RegisterRoute(Routes.Settings, typeof(SettingsPage));
		Routing.RegisterRoute(Routes.About, typeof(AboutPage));
	}
}
