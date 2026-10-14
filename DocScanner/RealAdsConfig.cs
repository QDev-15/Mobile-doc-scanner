namespace DocScanner;

/// <summary>
/// PRODUCTION ad unit IDs (LevelPlay dashboard, app key in <see cref="LevelPlayConfig.AppKey"/>). Used only by a build made with
/// <c>-p:AdsMode=Pro</c> (DocScanner.csproj), the one build that goes to Play for real:
/// <code>dotnet publish DocScanner.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=aab -p:AdsMode=Pro</code>
/// Even then <see cref="HasRealIds"/> must be true (both IDs filled in, not a "REPLACE_ME" placeholder), otherwise
/// <see cref="AdsMode"/> falls back to the Dev rules so a half-configured Pro build never requests a placeholder ID.
/// </summary>
public static class RealAdsConfig
{
    public const string BannerAdUnitId = "dc8xtbcrecuhnlop";
    public const string InterstitialAdUnitId = "0syfvxfg425xbspt";

    /// <summary>False until both ad unit IDs above are real ones.</summary>
    public static bool HasRealIds => !BannerAdUnitId.Contains("REPLACE_ME") && !InterstitialAdUnitId.Contains("REPLACE_ME")
        && BannerAdUnitId.Length > 0 && InterstitialAdUnitId.Length > 0;
}
