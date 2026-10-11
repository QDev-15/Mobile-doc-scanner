using DocScanner.Core.Licensing;

namespace DocScanner.Services;

/// <summary>The "buy Pro" prompt behind the crown button on the main screen: what Pro gives, the price once Play has
/// answered, and the purchase itself, all in one dialog (no extra screen). Success switches ads off right away through
/// <see cref="ILicenseService.Changed"/> (the ads library suspends itself, see <c>AdsService</c>).</summary>
public sealed class ProUpgrade(ILicenseService license)
{
	public async Task ShowAsync()
	{
		if (license.State.IsPro)
		{
			await Shell.Current.DisplayAlertAsync("Doc Scanner Pro", "Bạn đã dùng bản Pro. Cảm ơn bạn!", "OK");
			return;
		}

		string price = license.ProPriceText is { } p ? $"Mua Pro · {p}" : "Mua Pro";
		bool buy = await Shell.Current.DisplayAlertAsync("Nâng cấp Doc Scanner Pro",
			"• Không còn quảng cáo (banner và quảng cáo xen kẽ)\n• Mua một lần, dùng vĩnh viễn\n• Ủng hộ tác giả tiếp tục phát triển ứng dụng",
			price, "Để sau");
		if (!buy) return;

		PurchaseOutcome outcome = await license.PurchaseProAsync();
		switch (outcome)
		{
			case PurchaseOutcome.Purchased:
			case PurchaseOutcome.AlreadyOwned:
				await Shell.Current.DisplayAlertAsync("Cảm ơn bạn!", "Đã nâng cấp Doc Scanner Pro. Không còn quảng cáo.", "OK");
				break;
			case PurchaseOutcome.Error:
				await Shell.Current.DisplayAlertAsync("Không mua được", license.LastError ?? "Có lỗi xảy ra, thử lại sau.", "OK");
				break;
		}
	}
}
