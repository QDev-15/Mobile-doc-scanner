namespace DocScanner;

/// <summary>
/// Unity LevelPlay settings for DEV builds (the default <c>AdsMode=Dev</c>, see DocScanner.csproj): the App Key (same in both modes)
/// and the SANDBOX ad unit IDs. The production ad unit IDs live in <see cref="RealAdsConfig"/> and are used only by a build made
/// with <c>-p:AdsMode=Pro</c>. Same split as PdfReader (LevelPlayConfig.cs / RealAdsConfig.cs / its AdsMode), so a build that is
/// merely uploaded to a Play testing track can never request the real ad units and so can never cause an invalid-click strike.
///
/// <para>Sandbox ad units are separate ad units you create in the LevelPlay dashboard for this app (e.g. "DocScanner Banner
/// Sandbox", "DocScanner Interstitial Sandbox"); paste their IDs below. Until then <see cref="HasSandboxIds"/> is false and
/// <see cref="AdsMode"/> decides what a Dev build does (see there): Debug on your own phone keeps using the production units
/// (the phone is a registered LevelPlay test device, so it gets test creatives), a Release build shows no ads at all.</para>
/// </summary>
public static class LevelPlayConfig
{
    public const string AppKey = "289043a3d";

    /// <summary>Sandbox banner ad unit ID (Dev mode). Empty until created in the LevelPlay dashboard.</summary>
    public const string SandboxBannerAdUnitId = "";

    /// <summary>Sandbox interstitial ad unit ID (Dev mode). Empty until created in the LevelPlay dashboard.</summary>
    public const string SandboxInterstitialAdUnitId = "";

    public static bool HasSandboxIds => SandboxBannerAdUnitId.Length > 0 && SandboxInterstitialAdUnitId.Length > 0;

    /// <summary>Ads master switch for this build, fed to <c>LevelPlayOptions.IsAdsEnabled</c>. True unless the build was
    /// made with <c>-p:AdsEnabled=false</c> (DocScanner.csproj defines ADS_DISABLED). Applies to every configuration.</summary>
#if ADS_DISABLED
    public const bool IsAdsEnabled = false;
#else
    public const bool IsAdsEnabled = true;
#endif

    /// <summary>Turns on LevelPlay's own adapter debug logging (<c>LevelPlay.SetAdaptersDebug</c>). Debug builds
    /// only -- does not affect which ad unit IDs are used (that is <see cref="AdsMode"/>), only how chatty the SDK's own
    /// logcat output is.</summary>
#if DEBUG
    public const bool TestMode = true;
#else
    public const bool TestMode = false;
#endif
}
