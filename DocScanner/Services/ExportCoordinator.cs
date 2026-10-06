using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocScanner.Core;
using DocScanner.Core.Licensing;

namespace DocScanner.Services;

/// <summary>
/// "Xuất PDF" from any screen (document, crop, result): runs the export with its own progress overlay
/// (<see cref="Views.ExportOverlay"/>, bound to this singleton), saves the PDF into the export library, copies it
/// straight into the phone's Downloads/DocScanner folder (owner's request, 2026-09-28c: no extra tap needed to find
/// it later with a file manager or another app), then offers Share / Save again / Open.
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

	[ObservableProperty]
	private bool isExporting;

	[ObservableProperty]
	private string exportText = "";

	[RelayCommand]
	private void Cancel() => _cts?.Cancel();

	public async Task ExportAsync(string docId)
	{
		if (IsExporting) return;
		DocumentRecord? doc = store.Get(docId);
		if (doc == null) return;
		if (store.Pages(docId).Count == 0)
		{
			await Shell.Current.DisplayAlertAsync("Xuất PDF", "Tài liệu chưa có trang nào.", "OK");
			return;
		}

		//if (!license.State.CanExport && !await OfferUpgradeAsync()) return;

		PdfQuality? quality = await AskQualityAsync();
		if (quality == null) return;

		string path = library.NewPath(doc.Name, DateTime.Now);
		_cts = new CancellationTokenSource();
		IsExporting = true;
		ExportText = "Đang chuẩn bị xuất PDF...";
		PdfExportResult? result = null;
		try
		{
			var progress = new UiProgress(p => ExportText = $"{p.Stage} {p.Done}/{p.Total}...");
			CancellationToken ct = _cts.Token;
			result = await Task.Run(() => exporter.ExportAsync(docId, path, progress, ct, quality), ct);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			IsExporting = false;
			await Shell.Current.DisplayAlertAsync("Không xuất được PDF", ex.Message, "OK");
			return;
		}
		finally
		{
			IsExporting = false;
			_cts.Dispose();
			_cts = null;
		}

		if (result == null) return;
		license.RecordExport(); // no-op once Pro; a cancelled or failed export above never reaches here
		ads.RegisterExport(); // every 5th free export: a full-screen ad now, before the share sheet below --
		                       // never during it (would fight the share intent) and never on a later screen
		string summary = $"{result.PageCount} trang · {result.Bytes / 1024.0:0} KB";
		if (result.SkippedPages.Count > 0)
			summary += $"\nBỏ qua trang {string.Join(", ", result.SkippedPages)} (ảnh lỗi hoặc chưa cắt được).";

		// Straight into Downloads/DocScanner, no extra tap: a failure here (old Android, or the folder is somehow not
		// writable) does not fail the export itself -- the file is already safe in the app's own export library, and
		// the action sheet below still offers "Lưu vào Tải xuống" as a manual fallback.
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
		await OfferActionsAsync(result.Path, doc.Name, $"Đã xuất PDF ({summary})", allowDelete: false);
	}

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

	/// <summary>Small / medium / high (see <see cref="PdfQuality"/>); the last choice is ticked and remembered. Null = cancelled.</summary>
	private static async Task<PdfQuality?> AskQualityAsync()
	{
		string last = Preferences.Default.Get(QualityPreference, PdfQuality.Medium.Key);
		string[] labels = PdfQuality.All.Select(q => q.Key == last ? q.Label + "  ✓" : q.Label).ToArray();
		string? pick = await Shell.Current.DisplayActionSheetAsync("Chất lượng PDF", "Huỷ", null, labels);
		int index = pick == null ? -1 : Array.IndexOf(labels, pick);
		if (index < 0) return null;
		PdfQuality chosen = PdfQuality.All[index];
		Preferences.Default.Set(QualityPreference, chosen.Key);
		return chosen;
	}

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
