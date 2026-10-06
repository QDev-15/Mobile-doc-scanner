using DocScanner.Core;
using DocScanner.Core.Ads;
using DocScanner.Core.Licensing;
using Plugin.AdMob.Services;

namespace DocScanner.Services;

/// <summary>
/// Whether ads should be visible right now, and the one place in the app that talks to AdMob --
/// mirrors <see cref="LicenseService"/> being the one place that talks to Play Billing. The interstitial
/// rule itself (every 5th PDF export) lives in <see cref="AdsPolicy"/> and is unit-tested on its own;
/// this class only wires that decision to the actual ad SDK and to <see cref="ILicenseService"/>.
/// </summary>
public interface IAdsService
{
    /// <summary>False once Pro is bought (or restored). Every page's <c>AdBannerSurface</c>
    /// (<see cref="DocScanner.Views.AdBannerSurface"/>) binds its own <c>IsVisible</c> to
    /// <c>ShowAds &amp;&amp; IsBannerLoaded</c> directly -- see that class. An interstitial from
    /// <see cref="RegisterExport"/> is a separate full-screen ad (its own native activity), so it never visually
    /// competes with whatever the banner underneath happens to show.</summary>
    bool ShowAds { get; }

    /// <summary>True only while the shared banner actually has a creative on screen right now -- false before
    /// the first load, while a periodic refresh is in flight, and whenever the last load/refresh attempt failed
    /// (no network, no fill, ...). The ONE source of truth for whether the banner's strip should take up any
    /// space at all; see <see cref="DocScanner.Views.AdBannerSurface"/>.</summary>
    bool IsBannerLoaded { get; }

    /// <summary>Raised whenever <see cref="ShowAds"/> or <see cref="IsBannerLoaded"/> may have changed.</summary>
    event Action? Changed;

    /// <summary>Called by the platform banner ad listener (<c>AdBannerSurfaceHandler</c>, Android) on every
    /// <c>OnAdLoaded</c> / <c>OnAdFailedToLoad</c>, including automatic refreshes -- the only writer of
    /// <see cref="IsBannerLoaded"/>. No-op if the value did not actually change (avoids firing
    /// <see cref="Changed"/>, and so re-measuring every live banner, on an unrelated refresh tick).</summary>
    void ReportBannerLoaded(bool loaded);

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
    private readonly IInterstitialAdService _interstitial;

    public AdsService(ILicenseService license, IInterstitialAdService interstitial)
    {
        _license = license;
        _interstitial = interstitial;
        _license.Changed += () => Changed?.Invoke();
        PrepareInterstitial(); // one kept ready at all times, so the 5th export rarely has to skip
    }

    public bool ShowAds => !_license.State.IsPro;

    public bool IsBannerLoaded { get; private set; }

    public event Action? Changed;

    public void ReportBannerLoaded(bool loaded)
    {
        if (IsBannerLoaded == loaded) return;
        IsBannerLoaded = loaded;
        Changed?.Invoke();
    }

    public void RegisterExport()
    {
        var state = new AdsState(Preferences.Default.Get(StateKey, 0));
        (AdsState next, bool show) = AdsPolicy.AfterExport(state, _license.State.IsPro);
        Preferences.Default.Set(StateKey, next.ExportsSinceLastInterstitial);

        if (show && _interstitial.IsAdLoaded)
        {
            try { _interstitial.ShowAd(); }
            catch (Exception ex) { Perf.Log($"ads: interstitial show failed: {ex.Message}"); }
        }
        PrepareInterstitial(); // whether shown, skipped, or not due yet: keep one ready for next time
    }

    private void PrepareInterstitial()
    {
        try { _interstitial.PrepareAd(AdsConfig.InterstitialId); }
        catch (Exception ex) { Perf.Log($"ads: interstitial preload failed: {ex.Message}"); }
    }
}
