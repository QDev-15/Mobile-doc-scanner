using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DocScanner.Services;

/// <summary>
/// The app's own confirm / text-prompt / message dialog for the main screen, replacing the system
/// <c>DisplayAlertAsync</c> / <c>DisplayPromptAsync</c> (square, flat grey; owner's report 2026-10-13) with the same rounded,
/// shadowed card as <see cref="Views.ExportOverlay"/> and <see cref="ActionMenu"/>. One singleton, drawn by
/// <see cref="Views.AppDialogOverlay"/>; at most one dialog is open at a time (a new one cancels a stale one).
/// Same contracts as the system calls: <see cref="ConfirmAsync"/> = true when accepted, <see cref="PromptAsync"/> = the typed text
/// or null when cancelled.
/// </summary>
public sealed partial class AppDialog : ObservableObject
{
	private TaskCompletionSource<bool>? _pending;

	[ObservableProperty]
	private bool isOpen;

	[ObservableProperty]
	private string title = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasMessage))]
	private string message = "";

	public bool HasMessage => !string.IsNullOrEmpty(Message);

	[ObservableProperty]
	private string acceptText = "OK";

	[ObservableProperty]
	private string cancelText = "Huỷ";

	/// <summary>False for a plain message (one OK button).</summary>
	[ObservableProperty]
	private bool showCancel = true;

	/// <summary>Red accept button (deleting).</summary>
	[ObservableProperty]
	private bool isDestructive;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasIcon))]
	private string? icon;

	public bool HasIcon => !string.IsNullOrEmpty(Icon);

	[ObservableProperty]
	private bool showInput;

	[ObservableProperty]
	private string inputText = "";

	[ObservableProperty]
	private string inputPlaceholder = "";

	[ObservableProperty]
	private int inputMaxLength = 120;

	public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Huỷ", bool destructive = false, string? icon = null) =>
		OpenAsync(title, message, accept, cancel, showCancel: true, destructive, icon, showInput: false, "", "", 0);

	public Task AlertAsync(string title, string message, string ok = "OK", string? icon = null) =>
		OpenAsync(title, message, ok, "", showCancel: false, destructive: false, icon, showInput: false, "", "", 0);

	/// <summary>The typed text (possibly empty) when accepted, null when cancelled.</summary>
	public async Task<string?> PromptAsync(string title, string message, string accept, string cancel = "Huỷ", string initial = "",
		string placeholder = "", int maxLength = 120)
	{
		bool ok = await OpenAsync(title, message, accept, cancel, showCancel: true, destructive: false, icon: null, showInput: true,
			initial, placeholder, maxLength);
		return ok ? InputText : null;
	}

	private Task<bool> OpenAsync(string title, string message, string accept, string cancel, bool showCancel, bool destructive, string? icon,
		bool showInput, string initial, string placeholder, int maxLength)
	{
		Close(false); // a dialog that was somehow still open is answered as cancelled, never left hanging

		Title = title;
		Message = message;
		AcceptText = accept;
		CancelText = cancel;
		ShowCancel = showCancel;
		IsDestructive = destructive;
		Icon = icon;
		ShowInput = showInput;
		InputText = initial;
		InputPlaceholder = placeholder;
		InputMaxLength = maxLength > 0 ? maxLength : 120;

		_pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		IsOpen = true;
		return _pending.Task;
	}

	[RelayCommand]
	private void Accept() => Close(true);

	[RelayCommand]
	private void Cancel() => Close(false);

	/// <summary>Closes the dialog as cancelled (Android Back); false when none was open.</summary>
	public bool Dismiss()
	{
		if (!IsOpen) return false;
		Close(false);
		return true;
	}

	private void Close(bool accepted)
	{
		IsOpen = false;
		TaskCompletionSource<bool>? pending = _pending;
		_pending = null;
		pending?.TrySetResult(accepted);
	}
}
