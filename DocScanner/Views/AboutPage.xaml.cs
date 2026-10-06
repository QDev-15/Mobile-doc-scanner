namespace DocScanner.Views;

/// <summary>About the app: author and contact, version, terms of use, privacy, copyright and third-party components.</summary>
public partial class AboutPage : ContentPage
{
	public const string SupportEmail = "nguyenquynhvp.ictu@gmail.com";
	public const string SupportPhone = "0988632841";

	public AboutPage()
	{
		InitializeComponent();
		VersionLabel.Text = $"Phiên bản {AppInfo.Current.VersionString} (bản dựng {AppInfo.Current.BuildString})";
	}

	private async void OnEmail(object? sender, TappedEventArgs e)
	{
		string subject = Uri.EscapeDataString($"Doc Scanner {AppInfo.Current.VersionString} - hỗ trợ");
		try { await Launcher.Default.OpenAsync($"mailto:{SupportEmail}?subject={subject}"); }
		catch (Exception) { await Clipboard.Default.SetTextAsync(SupportEmail); await DisplayAlertAsync("Email", "Đã chép địa chỉ email.", "OK"); }
	}

	private async void OnPhone(object? sender, TappedEventArgs e)
	{
		try { await Launcher.Default.OpenAsync($"tel:{SupportPhone}"); }
		catch (Exception) { await Clipboard.Default.SetTextAsync(SupportPhone); await DisplayAlertAsync("Điện thoại", "Đã chép số điện thoại.", "OK"); }
	}
}
