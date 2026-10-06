namespace DocScanner.Core.Licensing;

/// <summary>
/// Where the trial stands, and whether the paid unlock ("Pro") has been bought. This is pure data +
/// pure logic (no Play Billing, no storage), so it is fully unit-testable; only obtaining the two
/// numbers it is built from -- <see cref="IsPro"/> and how many free exports have been used -- talks
/// to Android.
/// </summary>
public sealed record LicenseState(bool IsPro, int ExportsUsed, int FreeExportLimit)
{
    /// <summary>How many free PDF exports are left before Pro is required. Never negative; meaningless
    /// (but harmless) once <see cref="IsPro"/> is true.</summary>
    public int ExportsRemaining => Math.Max(0, FreeExportLimit - ExportsUsed);

    /// <summary>Whether an export may proceed without buying Pro.</summary>
    public bool CanExport => IsPro || ExportsRemaining > 0;

    /// <summary>Short line for the Settings screen / Home banner. Free is unlimited (no export count-down
    /// anymore - see ExportCoordinator.ExportAsync: the trial block is disabled, only the every-5th-export
    /// interstitial from AdsPolicy remains), so this only ever distinguishes Pro from free, never a remaining count.</summary>
    public string SummaryText => IsPro ? "Đã nâng cấp Pro" : "Đang dùng bản miễn phí (có quảng cáo)";
}

/// <summary>
/// The trial rule: a fixed number of free PDF exports, no time limit, no limit on scanning / editing.
/// Chosen over a day-based trial because it cannot be reset by turning back the phone's clock, and it
/// only ever interrupts the user at the exact moment they get a deliverable (a finished PDF), never
/// while they are still scanning or fixing up pages.
/// </summary>
public static class TrialPolicy
{
    /// <summary>Number of PDF exports a fresh install gets for free. A constant, not a magic number
    /// scattered around: change it here and both the app and its tests follow.</summary>
    public const int FreeExportLimit = 5;

    public static LicenseState Evaluate(bool isPro, int exportsUsed) => new(isPro, Math.Max(0, exportsUsed), FreeExportLimit);
}
