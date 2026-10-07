using DocScanner.AdsService.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using View = Android.Views.View;

namespace DocScanner.AdsService;

/// <summary>Maps <see cref="AdBannerSurface"/> to ONE shared native ad view for the whole app, whichever
/// provider is configured -- migrated from the app's own former <c>AdBannerSurfaceHandler</c> (2026-10-07),
/// generalised to ask the registered <see cref="IAdProvider"/> for the native view instead of constructing an
/// AdMob <c>AdView</c> directly. See the app's history for why a shared instance through a real Handler, rather
/// than a fresh native view per page or a hand-wired Activity-level one, is what actually fixes both the
/// performance problem and the banner not showing at all.</summary>
internal sealed class AdBannerSurfaceHandler : ViewHandler<AdBannerSurface, View>
{
    public static readonly IPropertyMapper<AdBannerSurface, AdBannerSurfaceHandler> Mapper =
        new PropertyMapper<AdBannerSurface, AdBannerSurfaceHandler>(ViewHandler.ViewMapper);

    public AdBannerSurfaceHandler() : base(Mapper)
    {
    }

    protected override View CreatePlatformView()
    {
        IAdProvider provider = IPlatformApplication.Current?.Services.GetService<IAdProvider>()
            ?? throw new InvalidOperationException("DocScanner.AdsService: MauiAppBuilder.UseAdsService(...) was never called.");
        return provider.CreateBannerView(Context);
    }

    /// <summary>Never tears down the shared native view just because the page hosting it right now is going
    /// away -- the next page's handler will simply reparent it (<see cref="CreatePlatformView"/>).</summary>
    protected override void DisconnectHandler(View platformView)
    {
    }
}
