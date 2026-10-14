using DocScanner.Core.Licensing;

namespace DocScanner.Services;

/// <summary>The "buy Pro" prompt behind the crown button on the main screen: what Pro gives, the price once Play has
/// answered, and the purchase itself, all in the app's own rounded dialog (<see cref="AppDialog"/>, no extra screen). Success
/// switches ads off right away through <see cref="ILicenseService.Changed"/> (the ads library suspends itself, see
/// <c>AdsService</c>).</summary>
public sealed class ProUpgrade(ILicenseService license, AppDialog dialog)
{
	private const string CrownIcon = "crown.svg";

	public async Task ShowAsync()
	{
		if (license.State.IsPro)
		{
			await dialog.AlertAsync("Doc Scanner Pro", "Bạn đã dùng bản Pro. Cảm ơn bạn!", icon: CrownIcon);
			return;
		}

		string price = license.ProPriceText is { } p ? $"Mua Pro · {p}" : "Mua Pro";
		bool buy = await dialog.ConfirmAsync("Nâng cấp Pro",
			"• Không còn quảng cáo (banner và quảng cáo xen kẽ)\n• Mua một lần, dùng vĩnh viễn\n• Ủng hộ tác giả tiếp tục phát triển ứng dụng",
			price, "Để sau", icon: CrownIcon);
		if (!buy) return;

		PurchaseOutcome outcome = await license.PurchaseProAsync();
		switch (outcome)
		{
			case PurchaseOutcome.Purchased:
			case PurchaseOutcome.AlreadyOwned:
				await dialog.AlertAsync("Cảm ơn bạn!", "Đã nâng cấp Doc Scanner Pro. Không còn quảng cáo.", icon: CrownIcon);
				break;
			case PurchaseOutcome.Error:
				await dialog.AlertAsync("Không mua được", license.LastError ?? "Có lỗi xảy ra, thử lại sau.");
				break;
		}
	}
}
