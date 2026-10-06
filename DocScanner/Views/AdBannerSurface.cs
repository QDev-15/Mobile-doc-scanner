using DocScanner.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DocScanner.Views;

/// <summary>Cross-platform placeholder for the free-tier ad banner: a MAUI View with a custom Handler
/// (<c>Platforms/Android/AdBannerSurfaceHandler.cs</c>) that hands every page the SAME native AdView instead of
/// building a fresh one each time.
///
/// Drop this at the bottom of any page -- no <c>HeightRequest</c>, it sizes itself to the real AdView while
/// shown, and NOTHING (not even a reserved strip) while there is no ad to show, via the same <c>IsVisible</c>
/// every other collapsible row in this app already uses (<see cref="IAdsService.ShowAds"/> and
/// <see cref="IAdsService.IsBannerLoaded"/>, kept live for as long as this instance is on screen). Same spot
/// the old "AdBannerView" ContentView (removed 2026-10-05) used to sit in. That version, a later attempt that
/// wrapped the whole Activity's native root view in a column with one AdView below it (also 2026-10-05), and a
/// third that tried to collapse the row from inside the Handler by overriding its measure (2026-10-05 again) --
/// all three turned out wrong in different ways; see <c>AdBannerSurfaceHandler</c>'s doc comment for the first
/// and third, since the fix for each is what explains why this one is shaped the way it is.</summary>
public sealed class AdBannerSurface : View
{
	private IAdsService? _ads;

	public AdBannerSurface()
	{
		IsVisible = false; // nothing to show until proven otherwise, not even for one frame
		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
	}

	private void OnLoaded(object? sender, EventArgs e)
	{
		_ads ??= IPlatformApplication.Current?.Services.GetService<IAdsService>();
		if (_ads == null) return;
		_ads.Changed += Apply;
		Apply();
	}

	private void OnUnloaded(object? sender, EventArgs e)
	{
		if (_ads != null) _ads.Changed -= Apply;
	}

	private void Apply() => IsVisible = _ads is { ShowAds: true, IsBannerLoaded: true };
}
