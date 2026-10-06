using Android.Gms.Ads;
using Android.Views;
using DocScanner.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;

namespace DocScanner.Views;

/// <summary>Maps <see cref="AdBannerSurface"/> to ONE shared native <c>AdView</c> for the whole app -- see
/// <see cref="AdBannerSurface"/>'s doc comment for why a shared instance through a real Handler, rather than a
/// fresh AdView per page or a hand-wired Activity-level one, is what actually fixes both the performance problem
/// and the banner not showing at all.
///
/// Showing/hiding the banner -- and collapsing its row to nothing when there is no creative to show -- is NOT
/// this class's job: it only creates the native AdView, reparents it as pages come and go, and reports load /
/// fail events to <see cref="IAdsService.ReportBannerLoaded"/>. <see cref="AdBannerSurface"/> itself owns
/// <c>IsVisible</c> off that state. An earlier version tried to do the collapsing HERE, by overriding
/// <c>GetDesiredSize</c> to report (0, 0) and calling <c>InvalidateMeasure()</c> on whichever page was current --
/// it worked going from hidden to shown (confirmed on-device), but NOT the other way round: once the row had
/// been measured at the banner's real height, a later remeasure reporting (0, 0) did not reliably shrink it back
/// (owner report, 2026-10-05: banner loaded fine on Wi-Fi, then going offline stopped the ad but the empty strip
/// stayed). <c>IsVisible</c> is what every other collapsible row in this app already uses (the selection bar,
/// the bottom bar) and it is Grid's own normal measure/arrange skip for an invisible child in both directions,
/// not a custom one just for this control.</summary>
internal sealed class AdBannerSurfaceHandler : ViewHandler<AdBannerSurface, AdView>
{
	public static readonly IPropertyMapper<AdBannerSurface, AdBannerSurfaceHandler> Mapper =
		new PropertyMapper<AdBannerSurface, AdBannerSurfaceHandler>(ViewHandler.ViewMapper);

	private static AdView? _shared;

	public AdBannerSurfaceHandler() : base(Mapper)
	{
	}

	protected override AdView CreatePlatformView()
	{
		if (_shared is { } existing)
		{
			// Already shown on another (still-alive, e.g. back-stack) page: an Android View can only have one
			// parent, so take it back before this page's own container tries to add it.
			(existing.Parent as ViewGroup)?.RemoveView(existing);
			return existing;
		}

		IAdsService? ads = IPlatformApplication.Current?.Services.GetService<IAdsService>();
		string adUnitId = Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds
			? "ca-app-pub-3940256099942544/6300978111" // Google's published test banner id (developers.google.com/admob/android/test-ads)
			: AdsConfig.BannerAdUnitId;
		_shared = new AdView(Context) { AdUnitId = adUnitId, AdSize = AdSize.Banner, AdListener = new LoadListener(ads) };
		if (ads?.ShowAds != false) _shared.LoadAd(new AdRequest.Builder().Build()); // already Pro at startup: skip the request entirely
		return _shared;
	}

	/// <summary>Never tears down the shared AdView just because the page hosting it right now is going away --
	/// the next page's handler will simply reparent it (<see cref="CreatePlatformView"/>).</summary>
	protected override void DisconnectHandler(AdView platformView)
	{
	}

	private sealed class LoadListener(IAdsService? ads) : AdListener
	{
		public override void OnAdLoaded() => ads?.ReportBannerLoaded(true);

		// No creative to show: first load with no network, a periodic refresh that came back empty, ...
		public override void OnAdFailedToLoad(LoadAdError error) => ads?.ReportBannerLoaded(false);
	}
}
