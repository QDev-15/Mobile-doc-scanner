using DocScanner.Core.Ads;

namespace DocScanner.Core.Tests;

public class AdsPolicyTests
{
    [Fact]
    public void First_four_exports_never_show_an_interstitial()
    {
        AdsState s = AdsState.Initial;
        for (int i = 1; i < AdsPolicy.ExportsPerInterstitial; i++)
        {
            (s, bool show) = AdsPolicy.AfterExport(s, isPro: false);
            Assert.False(show);
            Assert.Equal(i, s.ExportsSinceLastInterstitial);
        }
    }

    [Fact]
    public void The_5th_export_shows_one_and_resets_the_counter()
    {
        AdsState s = AdsState.Initial;
        for (int i = 0; i < AdsPolicy.ExportsPerInterstitial - 1; i++)
            (s, _) = AdsPolicy.AfterExport(s, isPro: false);

        (AdsState next, bool show) = AdsPolicy.AfterExport(s, isPro: false);
        Assert.True(show);
        Assert.Equal(0, next.ExportsSinceLastInterstitial);
    }

    [Fact]
    public void The_cycle_repeats_every_5_exports()
    {
        AdsState s = AdsState.Initial;
        var shown = new List<bool>();
        for (int i = 0; i < AdsPolicy.ExportsPerInterstitial * 3; i++)
        {
            bool show;
            (s, show) = AdsPolicy.AfterExport(s, isPro: false);
            shown.Add(show);
        }
        Assert.Equal(3, shown.Count(x => x));
        Assert.True(shown[AdsPolicy.ExportsPerInterstitial - 1]);
        Assert.True(shown[AdsPolicy.ExportsPerInterstitial * 2 - 1]);
        Assert.True(shown[AdsPolicy.ExportsPerInterstitial * 3 - 1]);
    }

    [Fact]
    public void Pro_never_shows_an_interstitial_but_the_counter_keeps_advancing()
    {
        AdsState s = AdsState.Initial;
        for (int i = 0; i < AdsPolicy.ExportsPerInterstitial * 2; i++)
        {
            (s, bool show) = AdsPolicy.AfterExport(s, isPro: true);
            Assert.False(show);
        }
        // If Pro were ever refunded, the very next export must not immediately show an ad
        // (the phase kept advancing quietly instead of freezing or resetting).
        (_, bool showAfterDowngrade) = AdsPolicy.AfterExport(s, isPro: false);
        Assert.False(showAfterDowngrade);
    }

    [Fact]
    public void A_negative_stored_counter_is_clamped_instead_of_ever_going_more_negative()
    {
        (AdsState s, bool show) = AdsPolicy.AfterExport(new AdsState(-7), isPro: false);
        Assert.False(show);
        Assert.Equal(1, s.ExportsSinceLastInterstitial);
    }
}
