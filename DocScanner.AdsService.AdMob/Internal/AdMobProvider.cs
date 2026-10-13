using Android.Gms.Ads;
using Microsoft.Extensions.DependencyInjection;
using Plugin.AdMob.Services;
using View = Android.Views.View;
using ViewGroup = Android.Views.ViewGroup;

namespace DocScanner.AdsService.Internal;

/// <summary>AdMob wrapper, migrated from the app's own former <c>Services/AdsService.cs</c> +
/// <c>Platforms/Android/AdBannerSurfaceHandler.cs</c> (2026-10-07) with no behaviour change: one shared native
/// <see cref="AdView"/> reused across pages, loaded only once the UMP consent flow allows a request, plus the
/// preloaded-interstitial wrapper around <see cref="IInterstitialAdService"/>. <c>MauiAppBuilder.UseAdsService</c>
/// still calls Plugin.AdMob's own <c>UseAdMob(...)</c> extension for SDK init / <see cref="IInterstitialAdService"/>
/// / <see cref="IAdConsentService"/> DI registration -- this class only wires those into the generic
/// <see cref="IAdProvider"/> shape.</summary>
internal sealed class AdMobProvider(AdMobOptions options, IInterstitialAdService interstitial) : IAdProvider
{
    private AdView? _shared;
    private bool _suspended; // set by SetSuspended: the app turned ads off mid-session (e.g. a Pro purchase)
    private bool _consentSubscribed; // LoadWhenConsented's OnConsentInfoUpdated/OnConsentFormDismissed, at most once ever

    public bool IsBannerLoaded { get; private set; }

    public event Action? BannerStateChanged;

    public AdMobProvider Initialize()
    {
        if (options.TestDeviceIds is { Length: > 0 })
            MobileAds.RequestConfiguration = new RequestConfiguration.Builder().SetTestDeviceIds(options.TestDeviceIds).Build();
        PrepareInterstitial(); // one kept ready at all times, mirrors the app's former eager preload
        return this;
    }

    public View CreateBannerView(Android.Content.Context context)
    {
        if (_shared is { } existing)
        {
            // Already shown on another (still-alive, e.g. back-stack) page: an Android View can only have one
            // parent, so take it back before this page's own container tries to add it.
            (existing.Parent as ViewGroup)?.RemoveView(existing);
            return existing;
        }

        _shared = new AdView(context) { AdUnitId = options.BannerAdUnitId, AdSize = AdSize.Banner, AdListener = new LoadListener(this) };
        LoadWhenConsented(_shared);
        return _shared;
    }

    /// <summary>Never requests before the UMP consent flow says ads may be requested (in EEA/UK the form is
    /// still on screen at startup and a request sent now would be dropped, leaving the banner blank until the
    /// next refresh). Otherwise loads once right away; if not yet allowed, loads the first time consent info /
    /// the form settles into "can request". <see cref="_suspended"/> is re-checked at the moment the consent
    /// flow actually allows the request (not just when this method is called): otherwise a Pro purchase made
    /// while the consent dialog is still up would suspend the (still consent-blocked) banner, and the request
    /// would still go out later once the user dismisses that dialog. The subscription itself is only ever set up
    /// once per instance (<see cref="_consentSubscribed"/>): SetSuspended(false) calls this again on every
    /// resume, and re-subscribing every time would stack a new, never-removed handler pair each time.</summary>
    private void LoadWhenConsented(AdView view)
    {
        IAdConsentService? consent = IPlatformApplication.Current?.Services.GetService<IAdConsentService>();
        void Load() { if (!_suspended) view.LoadAd(new AdRequest.Builder().Build()); }
        if (consent is null || consent.CanRequestAds()) { Load(); return; }
        if (_consentSubscribed) return;
        _consentSubscribed = true;

        bool loaded = false;
        void TryLoad()
        {
            if (loaded || !consent.CanRequestAds()) return;
            loaded = true;
            MainThread.BeginInvokeOnMainThread(Load);
        }
        consent.OnConsentInfoUpdated += (_, _) => TryLoad();
        consent.OnConsentFormDismissed += (_, _) => TryLoad();
    }

    /// <summary>Pro bought mid-session: stop the banner refreshing and retrying, hide it; resume on false. (A Pro
    /// user at startup never reaches here: the provider is never even created for them, see AdsClient.)</summary>
    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_shared is not { } view) return;
            view.Visibility = suspended ? Android.Views.ViewStates.Gone : Android.Views.ViewStates.Visible;
            if (suspended)
            {
                view.Pause();
                SetLoaded(false);
            }
            else
            {
                view.Resume();
                LoadWhenConsented(view);
            }
        });
    }

    public bool IsInterstitialReady => interstitial.IsAdLoaded;

    public void PrepareInterstitial()
    {
        try { interstitial.PrepareAd(options.InterstitialAdUnitId); }
        catch { /* best-effort: a failed preload just means ShowInterstitialIfReady is a no-op next time */ }
    }

    public void ShowInterstitialIfReady()
    {
        if (!interstitial.IsAdLoaded) return;
        try { interstitial.ShowAd(); }
        catch { /* best-effort, see PrepareInterstitial */ }
    }

    private sealed class LoadListener(AdMobProvider owner) : AdListener
    {
        public override void OnAdLoaded() => owner.SetLoaded(true);

        // No creative to show: first load with no network, a periodic refresh that came back empty, ...
        public override void OnAdFailedToLoad(LoadAdError error) => owner.SetLoaded(false);
    }

    private void SetLoaded(bool loaded)
    {
        if (IsBannerLoaded == loaded) return;
        IsBannerLoaded = loaded;
        BannerStateChanged?.Invoke();
    }
}
