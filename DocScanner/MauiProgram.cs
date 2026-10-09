using DocScanner.AdsService;
using DocScanner.Core;
using DocScanner.Core.Licensing;
using DocScanner.Services;
using DocScanner.ViewModels;
using DocScanner.Views;
using ImageCoreService;
using Microsoft.Extensions.Logging;

namespace DocScanner;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// Stage timings to logcat (adb logcat -s DocScanPerf): cheap, and the only way to see real speeds on a phone.
		Perf.Sink = line => Android.Util.Log.Info("DocScanPerf", line);
		Perf.Log("startup: CreateMauiApp");
		Perf.Log($"ads: LevelPlay app {LevelPlayConfig.AppKey}, test mode {LevelPlayConfig.TestMode}");
		// GAID thật máy đang gửi lên LevelPlay -- đối chiếu với ID đăng ký "test device" trên dashboard (đợt
		// 2026-10-09: nghi Google Play Services cache ID cũ, không khớp ID hiện trong Cài đặt máy). Phải chạy
		// ngoài main thread (GetAdvertisingIdInfo là lời gọi chặn, ném lỗi nếu gọi trên main thread).
		Task.Run(() =>
		{
			try
			{
				var info = Google.Ads.Identifier.AdvertisingIdClient.GetAdvertisingIdInfo(Android.App.Application.Context);
				Perf.Log($"ads: GAID = {info?.Id}, limit tracking = {info?.IsLimitAdTrackingEnabled}");
			}
			catch (Exception ex)
			{
				Perf.Log($"ads: GAID read failed: {ex.Message}");
			}
		});

		var builder = MauiApp.CreateBuilder();

		// driver test id 63cbec62-1e9f-4b74-936b-7649210507d9
		builder
			.UseMauiApp<App>()
			// Ad provider: Unity LevelPlay (switched 2026-10-08 -- AdMob account closed by Google, appeal
			// pending; AppLovin not accepting new publishers). AdMobOptions/AppLovinOptions are both still fully
			// written and build-verified in DocScanner.AdsService (see CLAUDE.md) if either becomes usable again
			// later -- switching back is only changing which options record is constructed here.
			// Test device for this provider is registered on the LevelPlay dashboard itself (Settings > Testing
			// > this app's GAID), not anything read from code.
			.UseAdsService(new LevelPlayOptions(
				AppKey: LevelPlayConfig.AppKey,
				BannerAdUnitId: LevelPlayConfig.BannerAdUnitId,
				InterstitialAdUnitId: LevelPlayConfig.InterstitialAdUnitId,
				TestMode: LevelPlayConfig.TestMode))
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				fonts.AddFont("MaterialIcons-Regular.ttf", "Icons"); // Material Icons (Apache-2.0): glyphs in Views/Icons.cs
			})
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<Views.PdfScrollSurface, Views.PdfScrollSurfaceHandler>();
			});

		builder.Services.AddSingleton(_ => new DocumentStore(Path.Combine(FileSystem.AppDataDirectory, "documents")));
		builder.Services.AddSingleton<IImageService, AndroidImageService>();
		builder.Services.AddSingleton<IEdgeDetector, DocumentEdgeDetector>();
		builder.Services.AddSingleton<CropDetectionService>();
		builder.Services.AddSingleton(_ => new DocScanner.Core.Signatures.SignatureLibrary(Path.Combine(FileSystem.AppDataDirectory, "signatures")));
		builder.Services.AddSingleton<SignatureSession>();
		builder.Services.AddSingleton<CropRenderService>();
		builder.Services.AddSingleton(sp => new PageIngestQueue(
			sp.GetRequiredService<DocumentStore>(), sp.GetRequiredService<IImageService>(), sp.GetRequiredService<CropDetectionService>(),
			render: sp.GetRequiredService<CropRenderService>()) { Prerender = true });
		builder.Services.AddSingleton<PageEditService>();
		builder.Services.AddSingleton<ImportService>();
		builder.Services.AddSingleton<BackgroundImporter>();
		builder.Services.AddSingleton<IPhotoPicker, AndroidPhotoPicker>();
		builder.Services.AddSingleton<IPdfPicker, AndroidPdfPicker>();
		builder.Services.AddSingleton<IPhotoCapture, AndroidPhotoCapture>();
		builder.Services.AddSingleton<IDocumentCamera, AndroidDocumentCamera>();
		builder.Services.AddSingleton<PdfExportService>();
		builder.Services.AddSingleton(_ => new ExportLibrary(Path.Combine(FileSystem.AppDataDirectory, "exports")));
		builder.Services.AddSingleton<ExportCoordinator>();
		builder.Services.AddSingleton<IDownloadsService, AndroidDownloadsService>();
		builder.Services.AddSingleton<ILicenseService, LicenseService>();
		// Fully qualified: the "DocScanner.AdsService" namespace (the ads library) and this app's own
		// "DocScanner.Services.AdsService" class share a name, so unqualified "AdsService" here is ambiguous.
		builder.Services.AddSingleton<IAdsService, DocScanner.Services.AdsService>();
		builder.Services.AddSingleton<PermissionService>();
		builder.Services.AddSingleton<ImportCoordinator>();

		builder.Services.AddTransient<HomeViewModel>();
		builder.Services.AddTransient<DocumentViewModel>();
		builder.Services.AddTransient<CropViewModel>();
		builder.Services.AddTransient<ResultViewModel>();
		builder.Services.AddTransient<ExportsViewModel>();
		builder.Services.AddTransient<ViewerViewModel>();
		builder.Services.AddTransient<PdfViewerViewModel>();
		builder.Services.AddTransient<SignatureViewModel>();
		builder.Services.AddTransient<SettingsViewModel>();
		builder.Services.AddTransient<HomePage>();
		builder.Services.AddTransient<DocumentPage>();
		builder.Services.AddTransient<CropPage>();
		builder.Services.AddTransient<ResultPage>();
		builder.Services.AddTransient<ExportsPage>();
		builder.Services.AddTransient<ViewerPage>();
		builder.Services.AddTransient<PdfViewerPage>();
		builder.Services.AddTransient<SignaturePage>();
		builder.Services.AddTransient<SettingsPage>();
		builder.Services.AddTransient<AboutPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		MauiApp app = builder.Build();
		Perf.Log("startup: MauiApp built");
		return app;
	}
}
