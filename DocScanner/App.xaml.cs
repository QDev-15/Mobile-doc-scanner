namespace DocScanner;

public partial class App : Application
{
	private readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		Core.Perf.Log("startup: CreateWindow");
		// The start screen first; it builds the shell (main screen) while it animates, then hands over.
		return new Window(new Views.SplashPage(() =>
		{
			var shell = new AppShell();
			Core.Perf.Log("startup: AppShell created");
			return shell;
		}));
	}

	protected override void OnStart()
	{
		base.OnStart();
		// Re-check Play for the Pro purchase (picks up a refund/chargeback, or a purchase made while the
		// app was closed) without making startup wait on it: every screen already shows the locally
		// cached state instantly and updates itself when this finishes.
		try
		{
			Core.Licensing.ILicenseService? license = _services.GetService<Core.Licensing.ILicenseService>();
			if (license != null) _ = license.RefreshAsync().ContinueWith(t =>
				Core.Perf.Log(t.Exception == null ? "license: refreshed at startup" : "license refresh failed: " + t.Exception.Message));
		}
		catch (Exception ex) { Core.Perf.Log("license refresh failed: " + ex.Message); }
	}
}
