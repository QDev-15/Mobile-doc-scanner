namespace DocScanner;

/// <summary>
/// AdMob ad unit IDs the app uses. These are the <b>real</b> IDs from the owner's own AdMob console
/// (filled in 2026-09-29), so <see cref="HasRealIds"/> is true and <see cref="MauiProgram.CreateMauiApp"/>
/// no longer forces Google's test ads -- every build configuration, Debug or Release, now requests real
/// ads. Be careful testing on a device that isn't registered as a Test device in the AdMob console
/// (Settings &gt; Test devices): tapping a real ad on it risks an invalid-click flag on the account.
/// If these two ever need to go back to placeholders (e.g. a fresh AdMob app), <see cref="HasRealIds"/>
/// checks for the literal text "REPLACE_ME" in either string. The AdMob <b>App ID</b> (a different,
/// separate ID) is NOT here: it lives in <c>DocScanner.csproj</c>'s <c>AndroidManifestPlaceholders</c>
/// (a malformed App ID crashes the app at startup before any C# runs) -- also the real one now.
/// </summary>
public static class AdsConfig
{
    public const string BannerAdUnitId = "ca-app-pub-5182523644830048/3727793254";
    public const string InterstitialAdUnitId = "ca-app-pub-5182523644830048/3838550455";

    /// <summary>False until both ad unit IDs above have been replaced with real ones.</summary>
    public static bool HasRealIds => !BannerAdUnitId.Contains("REPLACE_ME") && !InterstitialAdUnitId.Contains("REPLACE_ME");
}
