namespace DocScanner;

/// <summary>
/// Unity LevelPlay credentials -- the equivalent of the old, now-deleted AdsConfig for AdMob, much simpler because
/// LevelPlay has no separate "test ad unit ID" concept: the same App Key / Ad Unit IDs below serve test
/// creatives automatically on devices registered as a test device in the LevelPlay dashboard (Settings >
/// Testing), and real ads everywhere else. There is nothing here that can accidentally ship a placeholder ID
/// the way AdMob's old "REPLACE_ME" guard worked -- these are the owner's real credentials from their own
/// LevelPlay dashboard (filled in 2026-10-08), used as-is in every build configuration.
/// </summary>
public static class LevelPlayConfig
{
    public const string AppKey = "289043a3d";
    public const string BannerAdUnitId = "dc8xtbcrecuhnlop";
    public const string InterstitialAdUnitId = "0syfvxfg425xbspt";

    /// <summary>Ads master switch for this build, fed to <c>LevelPlayOptions.IsAdsEnabled</c>. True unless the build was
    /// made with <c>-p:AdsEnabled=false</c> (DocScanner.csproj defines ADS_DISABLED). Applies to every configuration.</summary>
#if ADS_DISABLED
    public const bool IsAdsEnabled = false;
#else
    public const bool IsAdsEnabled = true;
#endif

    /// <summary>Turns on LevelPlay's own adapter debug logging (<c>LevelPlay.SetAdaptersDebug</c>). Debug builds
    /// only, same Debug/Release split the old AdMob-era AdsConfig.UseTestAds used -- does not affect which ad
    /// unit IDs are used, only how chatty the SDK's own logcat output is.</summary>
#if DEBUG
    public const bool TestMode = true;
#else
    public const bool TestMode = false;
#endif
}
