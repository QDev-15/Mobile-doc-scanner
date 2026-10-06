using Android.Gms.Ads;
using Android.Views;
using DocScanner.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using Plugin.AdMob.Services;

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
		if (AdsConfig.TestDeviceIds.Length > 0)
		{
			MobileAds.RequestConfiguration = new RequestConfiguration.Builder().SetTestDeviceIds(AdsConfig.TestDeviceIds).Build();
		}
		_shared = new AdView(Context) { AdUnitId = AdsConfig.BannerId, AdSize = AdSize.Banner, AdListener = new LoadListener(ads) };
		if (ads?.ShowAds != false) LoadWhenConsented(_shared, ads); // already Pro at startup: skip the request entirely
		return _shared;
	}

	/// <summary>Never requests before the UMP consent flow says ads may be requested (in EEA/UK the form is still on screen
	/// at startup and a request sent now would be dropped, leaving the banner blank until the next refresh). Otherwise loads
	/// once right away; if not yet allowed, loads the first time consent info / the form settles into "can request".</summary>
	private static void LoadWhenConsented(AdView view, IAdsService? ads)
	{
		IAdConsentService? consent = IPlatformApplication.Current?.Services.GetService<IAdConsentService>();
		void Load() => view.LoadAd(new AdRequest.Builder().Build());
		DocScanner.Core.Perf.Log($"ads: banner consent={(consent is null ? "none" : consent.CanRequestAds().ToString())}");
		if (consent is null || consent.CanRequestAds()) { Load(); return; }

		bool loaded = false;
		void TryLoad()
		{
			if (loaded || !consent.CanRequestAds() || ads?.ShowAds == false) return;
			loaded = true;
			MainThread.BeginInvokeOnMainThread(Load);
		}
		consent.OnConsentInfoUpdated += (_, _) => TryLoad();
		consent.OnConsentFormDismissed += (_, _) => TryLoad();
	}

	/// <summary>Never tears down the shared AdView just because the page hosting it right now is going away --
	/// the next page's handler will simply reparent it (<see cref="CreatePlatformView"/>).</summary>
	protected override void DisconnectHandler(AdView platformView)
	{
	}

	private sealed class LoadListener(IAdsService? ads) : AdListener
	{
		public override void OnAdLoaded() { DocScanner.Core.Perf.Log("ads: banner loaded"); ads?.ReportBannerLoaded(true); }

		// No creative to show: first load with no network, a periodic refresh that came back empty, ...
		public override void OnAdFailedToLoad(LoadAdError error) { DocScanner.Core.Perf.Log($"ads: banner failed {error.Code} {error.Message}"); ads?.ReportBannerLoaded(false); }
	}
}
