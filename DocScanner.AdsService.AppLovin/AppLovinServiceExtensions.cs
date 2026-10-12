using DocScanner.AdsService.Internal;
using Microsoft.Maui.Hosting;

namespace DocScanner.AdsService;

/// <summary>The one call the app makes to use AppLovin MAX for ads: <c>builder.UseAppLovinAds(new
/// AppLovinOptions(...))</c>. Registers the AppLovin provider, the generic <see cref="IAdsClient"/> facade
/// (DocScanner.AdsService.Core), and the shared <c>AdBannerSurface</c> handler. Nothing else in the app needs
/// to know AppLovin is the network in use. NOT called today and NOT referenced by DocScanner.csproj (AppLovin
/// is not accepting new publishers right now, and unlike AdMob, nothing else in the app has a direct
/// compile-time dependency on an AppLovin type) -- kept available so switching to it later is "reference this
/// project, call UseAppLovinAds instead of UseLevelPlayAds" and nothing else.</summary>
public static class AppLovinServiceExtensions
{
    public static MauiAppBuilder UseAppLovinAds(this MauiAppBuilder builder, AppLovinOptions options) =>
        builder.UseAdsCore(options.IsAdsEnabled, _ => new AppLovinProvider(options).Initialize(Android.App.Application.Context));
}
