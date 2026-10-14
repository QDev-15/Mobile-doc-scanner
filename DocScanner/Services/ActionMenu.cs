using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DocScanner.Services;

/// <summary>One row of an <see cref="ActionMenu"/>.</summary>
public sealed record MenuEntry(string Text, ICommand Command, bool IsDestructive);

/// <summary>
/// The in-app replacement for <c>Shell.DisplayActionSheetAsync</c> on the main screen (owner's report, 2026-10-13: the
/// system sheet has square corners and no shadow, unlike the app's own rounded, shadowed export dialogs). Same contract as
/// the system call -- <see cref="ShowAsync"/> returns the text of the tapped row, the destructive row's text, or the
/// cancel text when dismissed -- but drawn by <see cref="Views.ActionMenuOverlay"/> with the same Border + RoundRectangle
/// + Shadow shell as <see cref="Views.ExportOverlay"/>. One singleton, one overlay per screen that uses it.
/// </summary>
public sealed partial class ActionMenu : ObservableObject
{
	private TaskCompletionSource<string?>? _pending;
	private string _cancelText = "Huỷ";

	public ObservableCollection<MenuEntry> Items { get; } = [];

	[ObservableProperty]
	private bool isOpen;

	[ObservableProperty]
	private string title = "";

	[ObservableProperty]
	private string cancelLabel = "Huỷ";

	public Task<string?> ShowAsync(string title, string cancel, string? destructive, IEnumerable<string> items)
	{
		Close(_cancelText); // a menu that was somehow still open is answered as cancelled, never left hanging

		Title = title;
		CancelLabel = cancel;
		_cancelText = cancel;
		Items.Clear();
		foreach (string text in items) Items.Add(new MenuEntry(text, new Command(() => Close(text)), false));
		if (!string.IsNullOrEmpty(destructive)) Items.Add(new MenuEntry(destructive, new Command(() => Close(destructive)), true));

		_pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		IsOpen = true;
		return _pending.Task;
	}

	[RelayCommand]
	private void Cancel() => Close(_cancelText);

	/// <summary>Closes the menu as cancelled (Android Back); false when none was open.</summary>
	public bool Dismiss()
	{
		if (!IsOpen) return false;
		Close(_cancelText);
		return true;
	}

	private void Close(string? result)
	{
		IsOpen = false;
		TaskCompletionSource<string?>? pending = _pending;
		_pending = null;
		pending?.TrySetResult(result);
	}
}
