namespace DocScanner;

/// <summary>
/// AdMob ad unit IDs the app uses, and the ONE place that decides whether a build shows Google's test ads or
/// the real ones. Everything that loads an ad (the banner in <c>AdBannerSurfaceHandler</c>, the interstitial in
/// <c>AdsService</c>, the plugin defaults in <c>MauiProgram</c>) asks <see cref="BannerId"/> /
/// <see cref="InterstitialId"/> -- never the raw <c>Real*</c> constants -- so a build can't accidentally mix a
/// test banner with a real interstitial.
///
/// Test vs real:
/// <list type="bullet">
/// <item><b>Debug build</b>: always Google's test ads (<see cref="UseTestAds"/> is a compile-time <c>true</c>).
/// Tapping them is safe.</item>
/// <item><b>Release build</b>: real ads, unless the real IDs still contain "REPLACE_ME"
/// (<see cref="HasRealIds"/> false) -- then test ads, so an early build shows a working ad instead of nothing.</item>
/// <item>A Release build on your own phone is therefore REAL ads. Put that phone's hashed ID in
/// <see cref="TestDeviceIds"/> (it is printed to logcat on the first ad request, see below) and the SDK serves
/// test creatives on it even with real ad unit IDs, so clicking them cannot count as invalid traffic.</item>
/// </list>
/// The AdMob <b>App ID</b> (a different ID) is not here: it lives in <c>DocScanner.csproj</c>'s
/// <c>AndroidManifestPlaceholders</c> (a malformed App ID crashes the app at startup before any C# runs).
/// </summary>
public static class AdsConfig
{
    // Real IDs from the owner's own AdMob console (filled in 2026-09-29).
    public const string RealBannerAdUnitId = "ca-app-pub-5182523644830048/3727793254";
    public const string RealInterstitialAdUnitId = "ca-app-pub-5182523644830048/3838550455";

    // Google's published sample IDs (developers.google.com/admob/android/test-ads): always return test creatives.
    public const string TestBannerAdUnitId = "ca-app-pub-3940256099942544/6300978111";
    public const string TestInterstitialAdUnitId = "ca-app-pub-3940256099942544/1033173712";

    /// <summary>Hashed device IDs that always get test creatives, even with real ad unit IDs. Find yours by
    /// running a Release build once and filtering logcat for "Use RequestConfiguration.Builder().setTestDeviceIds":
    /// the SDK prints the exact value to paste here. Android emulators are already treated as test devices.</summary>
    public static readonly string[] TestDeviceIds = [];

    /// <summary>False if either real ID above was replaced back by a "REPLACE_ME" placeholder.</summary>
    public static bool HasRealIds => !RealBannerAdUnitId.Contains("REPLACE_ME") && !RealInterstitialAdUnitId.Contains("REPLACE_ME");

    /// <summary>True when this build must show Google's test ads. Debug: always. Release: only while the real IDs are missing.</summary>
#if DEBUG || TEST_ADS
    // TEST_ADS: a Release/AAB for testers, built with `-p:TestAds=true` (see DocScanner.csproj) -- optimised and signed like
    // the real release, but every ad unit is Google's test one, so testers can tap ads freely.
    public static bool UseTestAds => true;
#else
    public static bool UseTestAds => !HasRealIds;
#endif

    public static string BannerId => UseTestAds ? TestBannerAdUnitId : RealBannerAdUnitId;
    public static string InterstitialId => UseTestAds ? TestInterstitialAdUnitId : RealInterstitialAdUnitId;
}
