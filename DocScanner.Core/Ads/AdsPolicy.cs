namespace DocScanner.Core.Ads;

/// <summary>How many PDF exports since the last interstitial was shown. Persisted the same way as
/// <see cref="Licensing.LicenseState"/>'s export counter (see <c>AdsService</c>): a plain number, no
/// dates, so it cannot be reset by turning back the phone's clock.</summary>
public sealed record AdsState(int ExportsSinceLastInterstitial)
{
    public static readonly AdsState Initial = new(0);
}

/// <summary>
/// The interstitial rule, decided once with the owner: show a full-screen ad after every 5th PDF
/// export, never more often -- frequent enough to earn something from free users, rare enough that it
/// stays a background nuisance rather than a reason to uninstall or leave a bad review, and it always
/// keeps "buy Pro to remove ads" worth having. Pure data + pure logic (no AdMob, no Play), so the rule
/// itself is unit-tested without any ad SDK; only actually loading/showing an ad talks to Android
/// (<c>AdsService</c>, the one place in the app that talks to AdMob -- mirrors <c>LicenseService</c>
/// being the one place that talks to Play Billing).
/// </summary>
public static class AdsPolicy
{
    /// <summary>A fresh install / a Pro purchase both start this many exports away from the next ad.</summary>
    public const int ExportsPerInterstitial = 5;

    /// <summary>Call once, right after a PDF export finishes successfully (Pro or not -- the counter
    /// keeps counting in the background so it doesn't reset to a random phase if Pro is ever refunded).
    /// Returns the state to persist and whether an interstitial should be shown now. Never true when
    /// <paramref name="isPro"/> -- callers do not need to check <see cref="Licensing.LicenseState.IsPro"/>
    /// separately.</summary>
    public static (AdsState Next, bool ShowInterstitial) AfterExport(AdsState state, bool isPro)
    {
        int count = Math.Max(0, state.ExportsSinceLastInterstitial) + 1;
        if (count < ExportsPerInterstitial) return (state with { ExportsSinceLastInterstitial = count }, false);
        return (AdsState.Initial, !isPro);
    }
}
