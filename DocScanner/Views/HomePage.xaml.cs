using DocScanner.ViewModels;

namespace DocScanner.Views;

/// <summary>Folder rows and document rows of the main screen.</summary>
public sealed class HomeRowSelector : DataTemplateSelector
{
	public DataTemplate? Folder { get; set; }
	public DataTemplate? Document { get; set; }

	protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
		(item is FolderItem ? Folder : Document)!;
}

/// <summary>The main screen, at the top level or inside a folder (route "folder").</summary>
public partial class HomePage : ContentPage
{
	private readonly HomeViewModel _viewModel;

	public HomePage(HomeViewModel viewModel)
	{
		DocScanner.Core.Perf.Log("startup: HomePage ctor");
		InitializeComponent();
		DocScanner.Core.Perf.Log("startup: HomePage XAML inflated");
		BindingContext = _viewModel = viewModel;
		Loaded += (_, _) => DocScanner.Core.Perf.Log("startup: HomePage loaded");
		_viewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(HomeViewModel.IsSearching) && _viewModel.IsSearching) Search.Focus();
		};
	}

	protected override async void OnAppearing()
	{
		DocScanner.Core.Perf.Log("startup: Home appearing");
		base.OnAppearing();
		_viewModel.Attach();
		await _viewModel.RefreshAsync();
		DocScanner.Core.Perf.Log("startup: Home list loaded");
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_viewModel.Detach();
	}

	private DateTime _lastBackPress = DateTime.MinValue;

	/// <summary>Back leaves selection mode / search first. Inside a folder, returning false here is what lets
	/// Shell pop back out of it (same as the header's own back arrow, <see cref="HomeViewModel.GoBackCommand"/>)
	/// -- <c>base.OnBackButtonPressed()</c> is NOT that signal, it unconditionally returns false itself and does
	/// no popping of its own; a first attempt at "press Back again to exit" called it as a fallback believing it
	/// WAS the pop, so every press -- including deep inside a folder -- got swallowed by the exit-confirmation
	/// toast instead, and a folder could no longer be left at all (owner report, 2026-10-05). Only once neither
	/// applies AND there is no folder to pop out of is this the true top level, where a single Back used to exit
	/// the app outright (too easy to hit by accident, e.g. an edge swipe while scrolling) -- that case alone now
	/// needs a second press within 2 s.</summary>
	protected override bool OnBackButtonPressed()
	{
		if (_viewModel.HandleBack()) return true;
		if (_viewModel.InFolder) return false; // let Shell pop back out of the folder, exactly as before
		return ConfirmExit();
	}

	private bool ConfirmExit()
	{
		DateTime now = DateTime.UtcNow;
		if (now - _lastBackPress < TimeSpan.FromSeconds(2)) return false; // second press in time: let it exit for real
		_lastBackPress = now;
		if (Platform.CurrentActivity is { } activity)
			Android.Widget.Toast.MakeText(activity, "Nhấn Back lần nữa để thoát", Android.Widget.ToastLength.Short)!.Show();
		return true; // consume this press
	}
}
