namespace DocScanner.Views;

/// <summary>
/// The start screen, right after the system splash (same blue, so the two join up): the scanner mark grows in, a scan
/// bar sweeps over the page, name and version show. Meanwhile the main screen is built; it replaces this page with a
/// cut (no fade: a fading page shows the white window behind it) once both are done (at least <see cref="MinimumTime"/>, so the screen does not just flash).
/// </summary>
public partial class SplashPage : ContentPage
{
	private static readonly TimeSpan MinimumTime = TimeSpan.FromMilliseconds(1300);

	private readonly Func<Page> _main;
	private bool _started;

	public SplashPage(Func<Page> main)
	{
		InitializeComponent();
		_main = main;
		VersionLabel.Text = $"Phiên bản {AppInfo.Current.VersionString}";
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_started) return;
		_started = true;
		var shown = Task.Delay(MinimumTime);

		await Task.WhenAll(
			Mark.FadeToAsync(1, 350, Easing.CubicOut),
			Mark.ScaleToAsync(1, 450, Easing.SpringOut));
		_ = SweepAsync();

		await Task.Yield();
		Page main = _main(); // build the main screen while the animation runs
		await shown;
		if (Window != null) Window.Page = main;
	}

	/// <summary>The scan bar going down and up over the page mark until the main screen takes over.</summary>
	private async Task SweepAsync()
	{
		while (Window != null && Window.Page == this)
		{
			await ScanBar.TranslateToAsync(0, 104, 650, Easing.SinInOut);
			await ScanBar.TranslateToAsync(0, 62, 650, Easing.SinInOut);
		}
	}
}
