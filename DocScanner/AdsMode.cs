namespace DocScanner;

/// <summary>
/// Which LevelPlay ad units this build requests, decided ONCE here (the AdsMode MSBuild property in DocScanner.csproj, independent
/// of Debug/Release and of APK vs AAB; same idea as PdfReader's AdsMode):
/// <list type="bullet">
/// <item><b>Pro</b> (<c>-p:AdsMode=Pro</c>): the production units of <see cref="RealAdsConfig"/> -- the one build that goes to Play
/// for real. If those are not filled in yet it behaves as Dev.</item>
/// <item><b>Dev</b> (the default): the sandbox units of <see cref="LevelPlayConfig"/>. Without sandbox units: a Debug build (your own
/// phone, a registered LevelPlay test device) keeps using the production units because the device is a test device; a Release build
/// (for example an AAB uploaded to a Play testing track, where testers could tap a real ad) requests NO ads at all.</item>
/// </list>
/// The master switch <see cref="LevelPlayConfig.IsAdsEnabled"/> (<c>-p:AdsEnabled=false</c>) still wins over everything.
/// </summary>
public static class AdsMode
{
#if ADS_MODE_PRO
    public const bool IsPro = true;
#else
    public const bool IsPro = false;
#endif

    /// <summary>The ad unit IDs to request and where they came from; <c>null</c> = request none (ads are switched off for this build).</summary>
    public static (string Banner, string Interstitial, string Source)? Resolve()
    {
        if (IsPro && RealAdsConfig.HasRealIds)
            return (RealAdsConfig.BannerAdUnitId, RealAdsConfig.InterstitialAdUnitId, "production");
        if (LevelPlayConfig.HasSandboxIds)
            return (LevelPlayConfig.SandboxBannerAdUnitId, LevelPlayConfig.SandboxInterstitialAdUnitId, "sandbox");
#if DEBUG
        if (RealAdsConfig.HasRealIds)
            return (RealAdsConfig.BannerAdUnitId, RealAdsConfig.InterstitialAdUnitId, "production units on a Debug build (test device)");
#endif
        return null;
    }
}
