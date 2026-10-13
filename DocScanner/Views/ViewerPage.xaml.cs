using DocScanner.ViewModels;
#if ANDROID
using DocScanner.Services;
#endif

namespace DocScanner.Views;

/// <summary>The page viewer: the picture goes into the native view (full resolution, zoomable), not through an ImageSource.</summary>
public partial class ViewerPage : ContentPage
{
	private readonly ViewerViewModel _viewModel;
	private string? _pending;
	private bool _hasPending;

	public ViewerPage(ViewerViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = _viewModel = viewModel;
		_viewModel.PictureChanged += OnPictureChanged;
#if ANDROID
		_host = new ZoomImageHost(Picture) { Swipe = delta => _viewModel.GoCommand.Execute(delta) };
		Picture.HandlerChanged += (_, _) =>
		{
			if (!_hasPending) return;
			_hasPending = false;
			_host.ShowFile(_pending);
		};
#endif
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_viewModel.Attach();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_viewModel.Detach();
#if ANDROID
		// Frees the full-resolution bitmap (up to 4096px long edge, tens of MB) proactively instead of leaving it
		// for the GC to eventually collect -- mirrors PdfViewerPage.OnNavigatedFrom's own proactive Dispose() for
		// the same reason. Safe with ViewerViewModel.Detach() now clearing _shownPath: returning to this exact
		// page redisplays the picture instead of finding "nothing changed" and leaving the view blank.
		_host.Clear();
#endif
	}

#if ANDROID
	private readonly ZoomImageHost _host;

	private void OnPictureChanged(string? path)
	{
		if (Picture.Handler == null)
		{
			_pending = path;
			_hasPending = true;
			return;
		}
		_host.ShowFile(path);
	}
#else
	private void OnPictureChanged(string? path) { }
#endif
}
