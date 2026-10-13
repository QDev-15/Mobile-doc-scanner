using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocScanner.Core;
using DocScanner.Core.Licensing;

namespace DocScanner.Services;

/// <summary>
/// "Xuất PDF" from any screen (document, crop, result): asks for a file name (<see cref="Views.ExportOverlay"/>'s
/// name-prompt section, owner's request 2026-10-13), runs the export with its own progress overlay, saves the PDF
/// into the export library, copies it straight into the phone's Downloads/DocScanner folder (owner's request,
/// 2026-09-28c: no extra tap needed to find it later with a file manager or another app), then shows a result
/// dialog (same overlay, a different section) with Xem / Chia sẻ on success. All three states live in one
/// <see cref="Views.ExportOverlay"/> instance (placed on every screen "Xuất PDF" can be called from) so there is
/// one rounded-corner-and-shadow dialog shell for the whole flow instead of three different system dialog styles.
///
/// This is also the one place that enforces the trial: exporting is the deliverable the user actually
/// wants, so it is the point at which the free-use count is spent and, once spent, where Pro is asked
/// for -- scanning, editing and everything else stays free with no interruption. See
/// <see cref="ILicenseService"/> and MOBILE-STATUS.md ("License &amp; khuyến mãi").
/// </summary>
public partial class ExportCoordinator(DocumentStore store, PdfExportService exporter, ExportLibrary library, IDownloadsService downloads,
	ILicenseService license, IAdsService ads)
	: ObservableObject
{
	private CancellationTokenSource? _cts;
	private DocumentRecord? _pendingDoc;

	/// <summary>The scrim + dialog shell is up for any of the flow's three states (exactly one is ever true at a
	/// time): asking for a name, exporting, or showing the result.</summary>
	public bool ShowOverlay => IsAskingName || IsExporting || IsShowingResult;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowOverlay))]
	private bool isExporting;

	[ObservableProperty]
	private string exportText = "";

	[RelayCommand]
	private void Cancel() => _cts?.Cancel();

	#region Name prompt

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowOverlay))]
	private bool isAskingName;

	[ObservableProperty]
	private string pdfNameInput = "";

	public Task ExportAsync(string docId)
	{
		if (IsExporting || IsAskingName) return Task.CompletedTask;
		DocumentRecord? doc = store.Get(docId);
		if (doc == null) return Task.CompletedTask;
		if (store.Pages(docId).Count == 0)
		{
			ShowResult(false, "Xuất PDF", "Tài liệu chưa có trang nào.");
			return Task.CompletedTask;
		}

		//if (!license.State.CanExport && !await OfferUpgradeAsync()) return;

		_pendingDoc = doc;
		PdfNameInput = doc.Name;
		IsAskingName = true;
		return Task.CompletedTask;
	}

	[RelayCommand]
	private void CancelName()
	{
		IsAskingName = false;
		_pendingDoc = null;
	}

	[RelayCommand]
	private async Task ConfirmNameAsync()
	{
		if (_pendingDoc is not { } doc) return;
		IsAskingName = false;
		string name = string.IsNullOrWhiteSpace(PdfNameInput) ? doc.Name : PdfNameInput.Trim();
		await RunExportAsync(doc, name);
	}

	#endregion

	#region Running the export

	private async Task RunExportAsync(DocumentRecord doc, string name)
	{
		string path = library.PathForName(name);
		PdfExportResult? result = await RunCoreAsync(doc.Id, path, "Không xuất được PDF");
		if (result == null) return;

		license.RecordExport(); // no-op once Pro; a cancelled or failed export above never reaches here
		ads.RegisterExport(); // every 5th free export: a full-screen ad now, before the result dialog below --
		                       // never during it (would fight the share intent) and never on a later screen
		string summary = $"{result.PageCount} trang · {result.Bytes / 1024.0:0} KB";
		if (result.SkippedPages.Count > 0)
			summary += $"\nBỏ qua trang {string.Join(", ", result.SkippedPages)} (ảnh lỗi hoặc chưa cắt được).";

		// Straight into Downloads/DocScanner, no extra tap: a failure here (old Android, or the folder is somehow not
		// writable) does not fail the export itself -- the file is already safe in the app's own export library.
		if (downloads.IsSupported)
		{
			try
			{
				await downloads.SaveAsync(result.Path, Path.GetFileName(result.Path), "application/pdf");
				summary += "\nĐã lưu vào Tải xuống/DocScanner.";
			}
			catch (Exception ex)
			{
				summary += $"\nKhông tự lưu được vào Tải xuống ({ex.Message}); có thể lưu tay bên dưới.";
			}
		}
		ShowResult(true, doc.Name, summary, result.Path);
	}

	/// <summary>"Chia sẻ" from the dashboard's ⋮ menu (owner's request, 2026-10-13): straight to a temp file, no
	/// name prompt and never kept in the export library (unlike <see cref="ExportAsync"/>/<see cref="RunExportAsync"/>)
	/// -- a one-off hand-off to the share sheet, not a kept export, so the temp file is deleted again once sharing
	/// is done (whether the user actually picked a target app or dismissed the sheet; either way Share.RequestAsync
	/// has already returned by then, so the file is no longer needed).</summary>
	public async Task ShareDocumentAsync(string docId)
	{
		if (IsExporting || IsAskingName) return;
		DocumentRecord? doc = store.Get(docId);
		if (doc == null) return;
		if (store.Pages(docId).Count == 0)
		{
			ShowResult(false, "Chia sẻ", "Tài liệu chưa có trang nào.");
			return;
		}

		string path = Path.Combine(FileSystem.CacheDirectory, $"share_{Guid.NewGuid():N}.pdf");
		PdfExportResult? result = await RunCoreAsync(docId, path, "Không chia sẻ được");
		if (result == null) return;
		try
		{
			await Share.Default.RequestAsync(new ShareFileRequest { Title = doc.Name, File = new ShareFile(result.Path, "application/pdf") });
		}
		catch (Exception ex)
		{
			ShowResult(false, "Không chia sẻ được", ex.Message);
		}
		finally
		{
			try { File.Delete(result.Path); } catch (IOException) { /* best-effort cleanup of a temp file */ }
		}
	}

	/// <summary>Runs the export itself with the shared progress overlay: null on cancel (silent, the user asked for
	/// it) or on failure (a result dialog titled <paramref name="failureTitle"/> already shown before returning).</summary>
	private async Task<PdfExportResult?> RunCoreAsync(string docId, string path, string failureTitle)
	{
		// Not asked per export (owner's request, 2026-10-13): Settings already has this choice
		// (SettingsViewModel.PdfQualityIndex, same "pdf_quality" preference key), asking again here every time was
		// redundant.
		PdfQuality quality = PdfQuality.FromKey(Preferences.Default.Get(QualityPreference, PdfQuality.Medium.Key));

		_cts = new CancellationTokenSource();
		IsExporting = true;
		ExportText = "Đang chuẩn bị xuất PDF...";
		try
		{
			var progress = new UiProgress(p => ExportText = $"{p.Stage} {p.Done}/{p.Total}...");
			CancellationToken ct = _cts.Token;
			return await Task.Run(() => exporter.ExportAsync(docId, path, progress, ct, quality), ct);
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			IsExporting = false;
			ShowResult(false, failureTitle, ex.Message);
			return null;
		}
		finally
		{
			IsExporting = false;
			_cts.Dispose();
			_cts = null;
		}
	}

	#endregion

	#region Result dialog

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowOverlay))]
	private bool isShowingResult;

	[ObservableProperty]
	private bool resultSuccess;

	[ObservableProperty]
	private string resultTitle = "";

	[ObservableProperty]
	private string resultMessage = "";

	private string? _resultPath;

	private void ShowResult(bool success, string title, string message, string? path = null)
	{
		ResultSuccess = success;
		ResultTitle = title;
		ResultMessage = message;
		_resultPath = path;
		IsShowingResult = true;
	}

	[RelayCommand]
	private void DismissResult()
	{
		IsShowingResult = false;
		_resultPath = null;
	}

	[RelayCommand]
	private async Task ViewResultAsync()
	{
		if (_resultPath is not { } path) return;
		IsShowingResult = false;
		await ViewAsync(path);
	}

	[RelayCommand]
	private async Task ShareResultAsync()
	{
		if (_resultPath is not { } path) return;
		IsShowingResult = false;
		try
		{
			await Share.Default.RequestAsync(new ShareFileRequest { Title = ResultTitle, File = new ShareFile(path, "application/pdf") });
		}
		catch (Exception ex)
		{
			ShowResult(false, "Không chia sẻ được", ex.Message);
		}
	}

	#endregion

	/// <summary>The trial is used up: explains why, offers to buy Pro right here (no extra screen to
	/// navigate to and back from), and returns true only if that purchase went through, so the export
	/// this call interrupted can continue.</summary>
	private async Task<bool> OfferUpgradeAsync()
	{
		bool buy = await Shell.Current.DisplayAlertAsync("Đã hết lượt xuất PDF miễn phí",
			$"Bạn đã dùng hết {license.State.ExportsUsed} lượt xuất PDF miễn phí. Nâng cấp Pro"
			+ (license.ProPriceText != null ? $" ({license.ProPriceText})" : "")
			+ " để xuất không giới hạn và bỏ quảng cáo, dùng vĩnh viễn.",
			"Mua Pro", "Để sau");
		if (!buy) return false;

		IsExporting = true; // reuse the same overlay so the screen does not look unresponsive during the Play sheet
		ExportText = "Đang mở Google Play...";
		PurchaseOutcome outcome;
		try
		{
			outcome = await license.PurchaseProAsync();
		}
		finally
		{
			IsExporting = false;
		}

		if (outcome is PurchaseOutcome.Purchased or PurchaseOutcome.AlreadyOwned) return true;
		if (outcome == PurchaseOutcome.Error)
			await Shell.Current.DisplayAlertAsync("Không mua được", license.LastError ?? "Có lỗi xảy ra, thử lại sau.", "OK");
		return false;
	}

	private const string QualityPreference = "pdf_quality";

	/// <summary>The in-app PDF viewer on <paramref name="path"/>.</summary>
	public static Task ViewAsync(string path) =>
		Shell.Current.GoToAsync($"{AppShell.Routes.PdfViewer}?path={Uri.EscapeDataString(path)}");

	/// <summary>View / share / save / open with another app (and optionally delete) an exported PDF. Returns true when it
	/// was deleted.</summary>
	public async Task<bool> OfferActionsAsync(string path, string title, string heading, bool allowDelete, bool offerView = true)
	{
		const string view = "Xem", share = "Chia sẻ...", save = "Lưu vào Tải xuống", open = "Mở bằng app khác";
		var actions = new List<string>();
		if (offerView) actions.Add(view);
		actions.Add(share);
		if (downloads.IsSupported) actions.Add(save);
		actions.Add(open);

		string? choice = await Shell.Current.DisplayActionSheetAsync(heading, "Đóng", allowDelete ? "Xoá" : null, [.. actions]);
		try
		{
			switch (choice)
			{
				case view:
					await ViewAsync(path);
					break;
				case share:
					await Share.Default.RequestAsync(new ShareFileRequest { Title = title, File = new ShareFile(path, "application/pdf") });
					break;
				case save:
					string saved = await downloads.SaveAsync(path, Path.GetFileName(path), "application/pdf");
					await Shell.Current.DisplayAlertAsync("Đã lưu", $"Đã lưu vào Tải xuống/DocScanner:\n{saved}", "OK");
					break;
				case open:
					await Launcher.Default.OpenAsync(new OpenFileRequest(title, new ReadOnlyFile(path, "application/pdf")));
					break;
				case "Xoá":
					return library.Delete(path);
			}
		}
		catch (Exception ex)
		{
			await Shell.Current.DisplayAlertAsync("Lỗi", ex.Message, "OK");
		}
		return false;
	}

	/// <summary>Marshals progress to the UI thread; reports arriving after the export ended are dropped.</summary>
	private sealed class UiProgress(Action<ExportProgress> apply) : IProgress<ExportProgress>
	{
		public void Report(ExportProgress value) => MainThread.BeginInvokeOnMainThread(() => apply(value));
	}
}
