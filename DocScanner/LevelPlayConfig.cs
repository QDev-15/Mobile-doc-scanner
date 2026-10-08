namespace DocScanner;

/// <summary>
/// Unity LevelPlay credentials -- the equivalent of <see cref="AdsConfig"/> for AdMob, much simpler because
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

    /// <summary>Turns on LevelPlay's own adapter debug logging (<c>LevelPlay.SetAdaptersDebug</c>). Debug builds
    /// only, same split as <see cref="AdsConfig.UseTestAds"/> -- does not affect which ad unit IDs are used,
    /// only how chatty the SDK's own logcat output is.</summary>
#if DEBUG
    public const bool TestMode = true;
#else
    public const bool TestMode = false;
#endif
}
