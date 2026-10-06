using DocScanner.Core.Licensing;

namespace DocScanner.Core.Tests;

public class LicenseStateTests
{
    [Fact]
    public void A_fresh_install_gets_the_full_free_quota()
    {
        LicenseState s = TrialPolicy.Evaluate(isPro: false, exportsUsed: 0);
        Assert.False(s.IsPro);
        Assert.Equal(TrialPolicy.FreeExportLimit, s.ExportsRemaining);
        Assert.True(s.CanExport);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(3, 2)]
    [InlineData(5, 0)]
    public void Remaining_counts_down_as_exports_are_used(int used, int expectedRemaining)
    {
        LicenseState s = TrialPolicy.Evaluate(isPro: false, exportsUsed: used);
        Assert.Equal(expectedRemaining, s.ExportsRemaining);
        Assert.Equal(expectedRemaining > 0, s.CanExport);
    }

    [Fact]
    public void Exhausting_the_quota_blocks_export_until_pro()
    {
        LicenseState s = TrialPolicy.Evaluate(isPro: false, exportsUsed: TrialPolicy.FreeExportLimit);
        Assert.False(s.CanExport);
        Assert.Equal(0, s.ExportsRemaining);
    }

    [Fact]
    public void Remaining_never_goes_negative_even_if_more_exports_were_recorded_than_the_limit()
    {
        // e.g. the limit was lowered in an update after some installs already used more than the new cap.
        LicenseState s = TrialPolicy.Evaluate(isPro: false, exportsUsed: TrialPolicy.FreeExportLimit + 10);
        Assert.Equal(0, s.ExportsRemaining);
        Assert.False(s.CanExport);
    }

    [Fact]
    public void A_negative_exports_used_is_clamped_to_zero()
    {
        LicenseState s = TrialPolicy.Evaluate(isPro: false, exportsUsed: -3);
        Assert.Equal(TrialPolicy.FreeExportLimit, s.ExportsRemaining);
    }

    [Fact]
    public void Pro_can_always_export_regardless_of_the_counter()
    {
        LicenseState exhausted = TrialPolicy.Evaluate(isPro: true, exportsUsed: TrialPolicy.FreeExportLimit + 50);
        Assert.True(exhausted.CanExport);

        LicenseState fresh = TrialPolicy.Evaluate(isPro: true, exportsUsed: 0);
        Assert.True(fresh.CanExport);
    }

    /// <summary>Free exporting is unlimited (ExportCoordinator.ExportAsync no longer checks CanExport; only
    /// AdsPolicy's every-5th-export interstitial remains), so the summary must never show a remaining count -
    /// only whether Pro is bought, regardless of how many exports were recorded.</summary>
    [Fact]
    public void Summary_text_only_ever_distinguishes_pro_from_free()
    {
        Assert.Equal("Đã nâng cấp Pro", TrialPolicy.Evaluate(true, 0).SummaryText);
        Assert.Equal("Đã nâng cấp Pro", TrialPolicy.Evaluate(true, TrialPolicy.FreeExportLimit + 50).SummaryText);
        Assert.Equal(TrialPolicy.Evaluate(false, 0).SummaryText, TrialPolicy.Evaluate(false, 1).SummaryText);
        Assert.Equal(TrialPolicy.Evaluate(false, 0).SummaryText, TrialPolicy.Evaluate(false, TrialPolicy.FreeExportLimit + 50).SummaryText);
        Assert.DoesNotContain("còn", TrialPolicy.Evaluate(false, 1).SummaryText);
    }
}
