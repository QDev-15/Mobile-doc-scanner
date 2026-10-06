using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocScanner.Core;
using DocScanner.Core.Licensing;
using Plugin.AdMob.Services;

namespace DocScanner.ViewModels;

/// <summary>Every setting of the app in one place (each one is stored the moment it changes, in the same place the
/// screens read it from), the storage used, and the app information.</summary>
public partial class SettingsViewModel(DocumentStore store, ExportLibrary exportsLibrary, ILicenseService license, IAdConsentService consent) : ObservableObject
{
	/// <summary>Google UMP requires an always-reachable "privacy options" entry in regions where consent is required (EEA/UK...);
	/// elsewhere <see cref="IAdConsentService.IsPrivacyOptionsRequired"/> is false and the row stays hidden.</summary>
	public bool ShowPrivacyOptions => consent.IsPrivacyOptionsRequired();

	[RelayCommand]
	private void OpenPrivacyOptions() => consent.ShowPrivacyOptionsForm();

	// Keys shared with the screens that use them.
	private const string CameraAutoKey = "camera_auto_capture", OpenModeKey = "document_open_mode", PdfQualityKey = "pdf_quality";

	public IReadOnlyList<string> OpenModes { get; } = ["Xem (phóng to, lật trang)", "Sửa (khung, bộ lọc)"];
	public IReadOnlyList<string> PdfQualities { get; } = PdfQuality.All.Select(q => q.Label).ToList();

	[ObservableProperty]
	private bool cameraAutoCapture = Preferences.Default.Get(CameraAutoKey, true);

	[ObservableProperty]
	private int openModeIndex = Preferences.Default.Get(OpenModeKey, "view") == "view" ? 0 : 1;

	[ObservableProperty]
	private int pdfQualityIndex = Math.Max(0, PdfQuality.All.ToList().IndexOf(PdfQuality.FromKey(Preferences.Default.Get(PdfQualityKey, PdfQuality.Medium.Key))));

	[ObservableProperty]
	private string storageText = "";

	/// <summary>App Store cập nhật ứng dụng, không phải chính ứng dụng tự cập nhật (xem README mục Phát hành).</summary>
	public string VersionText => $"Phiên bản {AppInfo.Current.VersionString} (bản dựng {AppInfo.Current.BuildString})";

	partial void OnCameraAutoCaptureChanged(bool value) => Preferences.Default.Set(CameraAutoKey, value);
	partial void OnOpenModeIndexChanged(int value) => Preferences.Default.Set(OpenModeKey, value == 0 ? "view" : "edit");
	partial void OnPdfQualityIndexChanged(int value)
	{
		if (value >= 0 && value < PdfQuality.All.Count) Preferences.Default.Set(PdfQualityKey, PdfQuality.All[value].Key);
	}

	/// <summary>Space used on the phone by the documents (originals, renders) and the exported PDFs.</summary>
	public async Task LoadStorageAsync()
	{
		(long docs, int count, long pdfs, int pdfCount) = await Task.Run(() =>
		{
			long Size(string dir) => Directory.Exists(dir)
				? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
			IReadOnlyList<ExportedFile> exported = exportsLibrary.List();
			return (Size(store.Root), store.List().Count, exported.Sum(f => f.Bytes), exported.Count);
		});
		StorageText = $"{count} tài liệu · {ExportsViewModel.Size(docs)}\n"
			+ (pdfCount == 0 ? "Chưa có PDF đã xuất" : $"{pdfCount} PDF đã xuất · {ExportsViewModel.Size(pdfs)}");
	}

	[RelayCommand]
	private Task OpenAboutAsync() => Shell.Current.GoToAsync(AppShell.Routes.About);

	#region Nâng cấp Pro

	[ObservableProperty]
	private string licenseSummaryText = license.State.SummaryText;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsFree))]
	private bool isPro = license.State.IsPro;

	public bool IsFree => !IsPro;

	/// <summary>"79.000 ₫" once Play has answered; the buy button hides its price row until then rather
	/// than show a wrong or empty one.</summary>
	[ObservableProperty]
	private string? proPriceText = license.ProPriceText;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(LicenseIdle))]
	private bool isBusyWithLicense;

	public bool LicenseIdle => !IsBusyWithLicense;

	/// <summary>Call once when the screen holding this view model appears (and stop in its disappear),
	/// same pattern as every other screen that follows a background service.</summary>
	public void AttachLicense()
	{
		license.Changed += OnLicenseChanged;
		OnLicenseChanged();
		_ = license.RefreshAsync(); // re-check Play in case a purchase / refund happened elsewhere
	}

	public void DetachLicense() => license.Changed -= OnLicenseChanged;

	private void OnLicenseChanged() => MainThread.BeginInvokeOnMainThread(() =>
	{
		LicenseSummaryText = license.State.SummaryText;
		IsPro = license.State.IsPro;
		ProPriceText = license.ProPriceText;
	});

	[RelayCommand]
	private async Task BuyProAsync()
	{
		if (IsBusyWithLicense || IsPro) return;
		IsBusyWithLicense = true;
		try
		{
			PurchaseOutcome outcome = await license.PurchaseProAsync();
			switch (outcome)
			{
				case PurchaseOutcome.Purchased:
				case PurchaseOutcome.AlreadyOwned:
					await Shell.Current.DisplayAlertAsync("Cảm ơn bạn!", "Đã nâng cấp Doc Scanner Pro. Mọi giới hạn dùng thử được gỡ bỏ, không còn quảng cáo.", "OK");
					break;
				case PurchaseOutcome.Cancelled:
					break;
				case PurchaseOutcome.Error:
					await Shell.Current.DisplayAlertAsync("Không mua được", license.LastError ?? "Có lỗi xảy ra, thử lại sau.", "OK");
					break;
			}
		}
		finally
		{
			IsBusyWithLicense = false;
		}
	}

	[RelayCommand]
	private async Task RestorePurchasesAsync()
	{
		if (IsBusyWithLicense) return;
		IsBusyWithLicense = true;
		try
		{
			PurchaseOutcome outcome = await license.RestoreAsync();
			string message = outcome switch
			{
				PurchaseOutcome.Purchased or PurchaseOutcome.AlreadyOwned => "Đã khôi phục: tài khoản Google Play này đã mua Pro.",
				PurchaseOutcome.Error => license.LastError ?? "Không kiểm tra được, thử lại sau.",
				_ => "Không tìm thấy giao dịch mua Pro nào với tài khoản Google Play đang đăng nhập.",
			};
			await Shell.Current.DisplayAlertAsync("Khôi phục giao dịch", message, "OK");
		}
		finally
		{
			IsBusyWithLicense = false;
		}
	}

	#endregion
}
