using DocScanner.Core;
using DocScanner.Core.Ads;
using DocScanner.Core.Licensing;
using AdsClient = DocScanner.AdsService.IAdsClient;

namespace DocScanner.Services;

/// <summary>
/// Whether ads should be visible right now, and the one place in the app that applies "Pro" to ads --
/// mirrors <see cref="LicenseService"/> being the one place that talks to Play Billing. The interstitial
/// rule itself (every 5th PDF export) lives in <see cref="AdsPolicy"/> and is unit-tested on its own; the
/// actual ad network (AdMob / AppLovin) lives entirely in the reusable <c>DocScanner.AdsService</c> library --
/// this class only wires this app's Pro/export policy to that library's generic <see cref="AdsClient"/>, the
/// same relationship it had with Plugin.AdMob directly before the ad provider was made swappable (2026-10-07).
/// </summary>
public interface IAdsService
{
    /// <summary>False once Pro is bought (or restored). Every page's <c>AdBannerSurface</c>
    /// (<c>DocScanner.AdsService.AdBannerSurface</c>) reads <see cref="AdsClient.AreAdsEnabled"/> /
    /// <see cref="AdsClient.IsBannerLoaded"/> directly off the library, which this class keeps in sync with Pro
    /// status -- see the constructor below. An interstitial from <see cref="RegisterExport"/> is a separate
    /// full-screen ad (its own native activity), so it never visually competes with whatever the banner
    /// underneath happens to show.</summary>
    bool ShowAds { get; }

    /// <summary>True only while the shared banner actually has a creative on screen right now. The ONE source
    /// of truth for whether the banner's strip should take up any space at all.</summary>
    bool IsBannerLoaded { get; }

    /// <summary>Raised whenever <see cref="ShowAds"/> or <see cref="IsBannerLoaded"/> may have changed.</summary>
    event Action? Changed;

    /// <summary>Call once, right after a PDF export finishes -- success only; a cancelled or failed export
    /// must not advance the cycle, same rule as <see cref="ILicenseService.RecordExport"/>. Shows an
    /// interstitial when <see cref="AdsPolicy"/> says it is due and one happens to be ready; otherwise
    /// this cycle is skipped quietly (ads must never make the export flow wait or fail).</summary>
    void RegisterExport();
}

public sealed class AdsService : IAdsService
{
    private const string StateKey = "ads_exports_since_interstitial";

    private readonly ILicenseService _license;
    private readonly AdsClient _ads;

    public AdsService(ILicenseService license, AdsClient ads)
    {
        _license = license;
        _ads = ads;
        _ads.AreAdsEnabled = !_license.State.IsPro;
        _license.Changed += () =>
        {
            _ads.AreAdsEnabled = !_license.State.IsPro;
            Changed?.Invoke();
        };
        _ads.BannerStateChanged += () => Changed?.Invoke();
        _ads.PrepareInterstitial(); // one kept ready at all times, so the 5th export rarely has to skip
    }

    public bool ShowAds => !_license.State.IsPro;

    public bool IsBannerLoaded => _ads.IsBannerLoaded;

    public event Action? Changed;

    public void RegisterExport()
    {
        var state = new AdsState(Preferences.Default.Get(StateKey, 0));
        (AdsState next, bool show) = AdsPolicy.AfterExport(state, _license.State.IsPro);
        Preferences.Default.Set(StateKey, next.ExportsSinceLastInterstitial);

        if (show) _ads.ShowInterstitialIfReady(); // no-op quietly if not actually ready yet -- see IAdsClient
        _ads.PrepareInterstitial(); // whether shown, skipped, or not due yet: keep one ready for next time
    }
}
