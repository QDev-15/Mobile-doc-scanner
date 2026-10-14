using System.ComponentModel;
using DocScanner.Services;

namespace DocScanner.Views;

/// <summary>The rounded, shadowed dialog of <see cref="AppDialog"/>; binds to the app-wide instance by itself.</summary>
public partial class AppDialogOverlay : ContentView
{
	private AppDialog? _dialog;

	public AppDialogOverlay()
	{
		InitializeComponent();
		BindingContext = IPlatformApplication.Current?.Services.GetService<AppDialog>();
		Loaded += (_, _) =>
		{
			_dialog = BindingContext as AppDialog;
			if (_dialog != null) _dialog.PropertyChanged += OnDialogChanged;
		};
		Unloaded += (_, _) =>
		{
			if (_dialog != null) _dialog.PropertyChanged -= OnDialogChanged;
			_dialog = null;
		};
	}

	/// <summary>A text prompt opening puts the cursor in the field with the whole suggested text selected (keyboard up); closing
	/// ANY dialog drops focus and hides the keyboard, otherwise the keyboard stays and squeezes whatever comes next.</summary>
	private void OnDialogChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(AppDialog.IsOpen) || _dialog == null) return;
		if (_dialog.IsOpen)
		{
			if (!_dialog.ShowInput) return;
			MainThread.BeginInvokeOnMainThread(async () =>
			{
				await Task.Delay(150); // let the dialog become visible first, a focus request before that is ignored
				if (!InputEntry.Focus()) return;
				InputEntry.CursorPosition = 0;
				InputEntry.SelectionLength = InputEntry.Text?.Length ?? 0;
			});
		}
		else
		{
			MainThread.BeginInvokeOnMainThread(() =>
			{
				InputEntry.Unfocus();
				_ = InputEntry.HideSoftInputAsync(CancellationToken.None);
			});
		}
	}
}
