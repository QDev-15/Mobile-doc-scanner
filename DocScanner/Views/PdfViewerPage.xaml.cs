using DocScanner.ViewModels;
#if ANDROID
using DocScanner.Services;
#endif

namespace DocScanner.Views;

/// <summary>The PDF viewer: a continuous, pinch-zoomable scroll through every page (<c>PdfScrollView</c>), each
/// page rendered by the platform (PdfRenderer).</summary>
public partial class PdfViewerPage : ContentPage
{
	private readonly PdfViewerViewModel _viewModel;

	public PdfViewerPage(PdfViewerViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = _viewModel = viewModel;
#if ANDROID
		_viewModel.FileChanged += OnFileChanged;
		Surface.HandlerChanged += OnSurfaceHandlerChanged;
#endif
	}

#if ANDROID
	private PdfScrollView? _scroll;
	private PdfPages? _pages;
	private string? _pendingPath;

	/// <summary>Grabs the native view PdfScrollSurfaceHandler already created and sized -- see PdfScrollSurface's
	/// doc comment for why this needs a real Handler instead of being added as a plain native child.</summary>
	private void OnSurfaceHandlerChanged(object? sender, EventArgs e)
	{
		if (_scroll != null || Surface.Handler?.PlatformView is not PdfScrollView view) return;
		_scroll = view;
		_scroll.PageChanged += (index, count) => _viewModel.ReportVisiblePage(index, count);
		if (_pendingPath != null) OpenFile(_pendingPath);
	}

	private void OnFileChanged(string path)
	{
		if (_scroll == null) { _pendingPath = path; return; }
		OpenFile(path);
	}

	private void OpenFile(string path)
	{
		_pendingPath = null;
		_pages?.Dispose();
		_pages = null;
		try { _pages = new PdfPages(path); }
		catch (Exception ex)
		{
			_ = DisplayAlertAsync("Không mở được PDF", ex.Message, "OK");
		}
		_scroll!.SetPages(_pages);
	}

	protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
	{
		base.OnNavigatedFrom(args);
		// Leaving for good (back to the list), not just covered by another page: close the file.
		if (Navigation.NavigationStack.Contains(this)) return;
		_scroll?.Clear();
		_pages?.Dispose();
		_pages = null;
	}
#endif
}
