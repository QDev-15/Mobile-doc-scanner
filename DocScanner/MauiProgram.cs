using DocScanner.Core;
using DocScanner.Core.Licensing;
using DocScanner.Services;
using DocScanner.ViewModels;
using DocScanner.Views;
using ImageCoreService;
using Microsoft.Extensions.Logging;
using Plugin.AdMob; // UseAdMob() extension (Plugin.AdMob.Config)

namespace DocScanner;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// Stage timings to logcat (adb logcat -s DocScanPerf): cheap, and the only way to see real speeds on a phone.
		Perf.Sink = line => Android.Util.Log.Info("DocScanPerf", line);
		Perf.Log("startup: CreateMauiApp");

		// Ads: Debug always uses Google's own test ad units, whatever AdsConfig says, so development never
		// risks a policy strike from clicking a real ad. Release does too, UNLESS AdsConfig.HasRealIds --
		// otherwise a Release build made before AdMob is set up would request the still-placeholder IDs,
		// which AdMob rejects, and show no ad at all instead of a working test one.
#if DEBUG
		Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = true;
#else
		Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = !AdsConfig.HasRealIds;
#endif

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseAdMob(androidDefaultBannerAdUnitId: AdsConfig.BannerAdUnitId, androidDefaultInterstitialAdUnitId: AdsConfig.InterstitialAdUnitId)
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				fonts.AddFont("MaterialIcons-Regular.ttf", "Icons"); // Material Icons (Apache-2.0): glyphs in Views/Icons.cs
			})
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<Views.PdfScrollSurface, Views.PdfScrollSurfaceHandler>();
				handlers.AddHandler<Views.AdBannerSurface, Views.AdBannerSurfaceHandler>();
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
		builder.Services.AddSingleton<IAdsService, AdsService>();
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
