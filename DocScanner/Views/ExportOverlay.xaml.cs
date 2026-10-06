using DocScanner.Services;

namespace DocScanner.Views;

/// <summary>Progress overlay of <see cref="ExportCoordinator"/>; binds to the app-wide instance by itself.</summary>
public partial class ExportOverlay : ContentView
{
	public ExportOverlay()
	{
		InitializeComponent();
		BindingContext = IPlatformApplication.Current?.Services.GetService<ExportCoordinator>();
	}
}
