using DocScanner.AdsService.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;
using Plugin.AdMob;
using Plugin.AdMob.Services;

namespace DocScanner.AdsService;

/// <summary>The one call the app makes to use AdMob for ads: <c>builder.UseAdMobAds(new AdMobOptions(...))</c>.
/// Registers the AdMob provider, the generic <see cref="IAdsClient"/> facade (DocScanner.AdsService.Core), and
/// the shared <c>AdBannerSurface</c> handler. Nothing else in the app needs to know AdMob is the network in
/// use. NOT called today and this project is NOT referenced by DocScanner.csproj at all (DocScanner/MauiProgram.cs
/// uses <c>UseLevelPlayAds</c> instead -- AdMob's account was closed by Google, appeal pending). The app's own
/// separate direct <c>Plugin.AdMob</c> PackageReference it used to keep for SettingsViewModel.cs's "Privacy
/// options" row is gone too (2026-10-12, that row was deleted along with it); kept available here so switching
/// back to AdMob later is "reference this project, call UseAdMobAds instead of UseLevelPlayAds" and nothing
/// else.</summary>
public static class AdMobServiceExtensions
{
    public static MauiAppBuilder UseAdMobAds(this MauiAppBuilder builder, AdMobOptions options)
    {
        // Plugin.AdMob's own extension: native Google Mobile Ads SDK init, UMP consent flow, and DI
        // registration of IInterstitialAdService / IAdConsentService. IAdConsentService used to also be a
        // direct dependency of this app's Settings page ("Privacy options" button), independent of which
        // provider serves ads; that row was removed along with the app's direct Plugin.AdMob reference.
        builder.UseAdMob(androidDefaultBannerAdUnitId: options.BannerAdUnitId, androidDefaultInterstitialAdUnitId: options.InterstitialAdUnitId);
        // Fully qualified: Microsoft.Maui.Controls.Compatibility (referenced by DocScanner.AdsService.Core, for
        // ViewHandler<,>) also declares a generic type simply named "Configuration<,>", which an unqualified
        // "Configuration" here resolves to instead of Plugin.AdMob's (CS0305).
        Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = options.UseTestAds;
        return builder.UseAdsCore(options.IsAdsEnabled, sp => new AdMobProvider(options, sp.GetRequiredService<IInterstitialAdService>()).Initialize());
    }
}
