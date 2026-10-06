using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;
using DocScanner.Core;
using AndroidUri = Android.Net.Uri;

namespace DocScanner.Services;

/// <summary>
/// Picks one PDF with the system document picker, then rasterizes every page with Android's own PdfRenderer
/// (built into the OS since Android 5: no library, no licence question - the same renderer PdfPages already
/// uses to display a PDF). Each page becomes one JPEG in the cache; it is fed into the normal photo-import
/// pipeline (ImportService/BackgroundImporter) exactly like a camera or gallery photo, so cropping, B&amp;W,
/// page management etc. all work on a PDF-imported page the same as on any other page. The cache file is
/// deleted once the import has copied it into the document (FileOptions.DeleteOnClose, same as the camera path
/// in ImportCoordinator.Source).
/// </summary>
public sealed class AndroidPdfPicker : IPdfPicker
{
	private const int RequestCode = 0x5046;

	// A4 at 300 DPI: matches the app's own "Cao" PDF export quality, plenty for a page that started as a PDF
	// (usually already clean, vector-rendered text/graphics with no camera noise to compensate for).
	private const int LongEdgePx = 3508;

	private static TaskCompletionSource<AndroidUri?>? _pending;

	public async Task<IReadOnlyList<ImportSource>> PickAsync()
	{
		Activity activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No activity.");
		_pending?.TrySetResult(null); // a picker left open by an earlier call
		var tcs = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending = tcs;

		var intent = new Intent(Intent.ActionGetContent);
		intent.SetType("application/pdf");
		intent.AddCategory(Intent.CategoryOpenable);
		activity.StartActivityForResult(intent, RequestCode);

		AndroidUri? uri = await tcs.Task;
		if (uri == null) return [];

		return await Task.Run(() => Rasterize(uri));
	}

	/// <summary>Called by MainActivity.OnActivityResult.</summary>
	public static bool OnActivityResult(int requestCode, Result resultCode, Intent? data)
	{
		if (requestCode != RequestCode) return false;
		TaskCompletionSource<AndroidUri?>? tcs = _pending;
		_pending = null;
		tcs?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
		return true;
	}

	private static List<ImportSource> Rasterize(AndroidUri uri)
	{
		ContentResolver resolver = Android.App.Application.Context.ContentResolver!;
		using ParcelFileDescriptor? pfd = resolver.OpenFileDescriptor(uri, "r")
			?? throw new IOException("Không mở được file PDF.");
		using var renderer = new PdfRenderer(pfd);

		var sources = new List<ImportSource>(renderer.PageCount);
		for (int i = 0; i < renderer.PageCount; i++)
		{
			string tempPath = System.IO.Path.Combine(FileSystem.CacheDirectory, $"pdfimport_{Guid.NewGuid():N}.jpg");
			RenderPage(renderer, i, tempPath);

			int pageNumber = i + 1;
			sources.Add(new ImportSource($"pdf_page{pageNumber}.jpg", _ => Task.FromResult<Stream>(
				new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.DeleteOnClose))));
		}
		return sources;
	}

	private static void RenderPage(PdfRenderer renderer, int index, string jpegPath)
	{
		// Close() explicitly: the renderer refuses to open another page while one is still open (same
		// "Current page not closed" issue PdfPages already works around for the viewer).
		PdfRenderer.Page page = renderer.OpenPage(index);
		try
		{
			double scale = (double)LongEdgePx / Math.Max(page.Width, page.Height);
			int w = Math.Max(1, (int)Math.Round(page.Width * scale)), h = Math.Max(1, (int)Math.Round(page.Height * scale));
			using Bitmap bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
			bitmap.EraseColor(Android.Graphics.Color.White);
			page.Render(bitmap, null, null, PdfRenderMode.ForDisplay);
			SaveJpeg(bitmap, jpegPath);
		}
		finally
		{
			page.Close();
			page.Dispose();
		}
	}

	/// <summary>Straight into a Java file stream (buffered): same trick AndroidImageService.SaveJpeg uses, so
	/// Bitmap.Compress never calls back into a managed stream for every few KB.</summary>
	private static void SaveJpeg(Bitmap bmp, string path)
	{
		bool ok;
		using (var file = new Java.IO.FileOutputStream(path))
		using (var buffered = new Java.IO.BufferedOutputStream(new Android.Runtime.OutputStreamInvoker(file), 1 << 16))
		using (var stream = new Android.Runtime.OutputStreamInvoker(buffered))
		{
			ok = bmp.Compress(Bitmap.CompressFormat.Jpeg!, 92, stream);
			buffered.Flush();
		}
		if (!ok) throw new IOException("Không ghi được ảnh từ trang PDF.");
	}
}
