using DocScanner.Services;

namespace DocScanner.Views;

/// <summary>The rounded, shadowed menu dialog of <see cref="ActionMenu"/>; binds to the app-wide instance by itself.</summary>
public partial class ActionMenuOverlay : ContentView
{
	public ActionMenuOverlay()
	{
		InitializeComponent();
		BindingContext = IPlatformApplication.Current?.Services.GetService<ActionMenu>();
	}
}
