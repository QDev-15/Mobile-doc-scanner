using System.ComponentModel;
using DocScanner.Services;

namespace DocScanner.Views;

/// <summary>Progress overlay of <see cref="ExportCoordinator"/>; binds to the app-wide instance by itself.</summary>
public partial class ExportOverlay : ContentView
{
	private ExportCoordinator? _coordinator;

	public ExportOverlay()
	{
		InitializeComponent();
		BindingContext = IPlatformApplication.Current?.Services.GetService<ExportCoordinator>();
		// Subscribe only while this overlay is on screen: every page owns one, all bound to the same singleton.
		Loaded += (_, _) =>
		{
			_coordinator = BindingContext as ExportCoordinator;
			if (_coordinator != null) _coordinator.PropertyChanged += OnCoordinatorChanged;
		};
		Unloaded += (_, _) =>
		{
			if (_coordinator != null) _coordinator.PropertyChanged -= OnCoordinatorChanged;
			_coordinator = null;
		};
	}

	/// <summary>When the "name the PDF" prompt opens, put the cursor in the field with the whole suggested name
	/// selected, so the keyboard is up and typing replaces it (or one tap on Xuất keeps it).</summary>
	private void OnCoordinatorChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(ExportCoordinator.IsAskingName) || _coordinator == null) return;
		if (!_coordinator.IsAskingName)
		{
			// The prompt closed: drop focus and hide the keyboard. Left open it squeezes the window, so the result dialog that
			// follows is pushed up and clipped (seen on device, 2026-10-13).
			MainThread.BeginInvokeOnMainThread(() =>
			{
				NameEntry.Unfocus();
				_ = NameEntry.HideSoftInputAsync(CancellationToken.None);
			});
			return;
		}
		MainThread.BeginInvokeOnMainThread(async () =>
		{
			await Task.Delay(150); // let the dialog become visible first, a focus request before that is ignored
			if (!NameEntry.Focus()) return;
			NameEntry.CursorPosition = 0;
			NameEntry.SelectionLength = NameEntry.Text?.Length ?? 0;
		});
	}
}
