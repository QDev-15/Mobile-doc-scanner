using DocScanner.Core;

namespace DocScanner.Services;

/// <summary>
/// Glue between the system pickers (gallery / camera) and <see cref="BackgroundImporter"/>. Picking only collects the
/// photos; the copying runs in the background, so the caller opens the document at once and watches it fill in.
/// The target document is created lazily, only after the user actually picked something, so backing out of a picker
/// never leaves an empty document behind.
/// </summary>
public sealed class ImportCoordinator(BackgroundImporter importer, IPhotoPicker picker, IPdfPicker pdfPicker,
	IPhotoCapture camera, IDocumentCamera scanner, PermissionService permissions)
{
	private bool _picking;

	/// <summary>The document the picked photos are going into, or null when nothing was picked.</summary>
	public async Task<DocumentRecord?> FromGalleryAsync(Func<DocumentRecord> document)
	{
		if (_picking) return null; // a second tap while the picker is opening
		_picking = true;
		try
		{
			IReadOnlyList<ImportSource> sources = await picker.PickAsync();
			if (sources.Count == 0) return null;
			return await CreateAndStartAsync(document, sources);
		}
		catch (Exception ex)
		{
			await ReportAsync("Không mở được thư viện ảnh", ex);
			return null;
		}
		finally
		{
			_picking = false;
		}
	}

	/// <summary>Picks one PDF and splits it into pages (one photo per PDF page), same background-copy flow as
	/// the gallery: picking only rasterizes, each page's JPEG is then handed to BackgroundImporter like any
	/// other photo, which is what runs crop detection / lets the user adjust the page etc.</summary>
	public async Task<DocumentRecord?> FromPdfAsync(Func<DocumentRecord> document)
	{
		if (_picking) return null;
		_picking = true;
		try
		{
			IReadOnlyList<ImportSource> sources = await pdfPicker.PickAsync();
			if (sources.Count == 0) return null;
			return await CreateAndStartAsync(document, sources);
		}
		catch (Exception ex)
		{
			await ReportAsync("Không đọc được file PDF", ex);
			return null;
		}
		finally
		{
			_picking = false;
		}
	}

	public async Task<DocumentRecord?> FromCameraAsync(Func<DocumentRecord> document)
	{
		try
		{
			return await CaptureIntoAsync(document);
		}
		catch (Exception ex)
		{
			// A camera problem must never take the app down (it did: MAUI's capture threw on Android 12).
			await ReportAsync("Không chụp được ảnh", ex);
			return null;
		}
	}

	private static Task ReportAsync(string title, Exception ex) =>
		Shell.Current.DisplayAlertAsync(title, ex.Message, "OK");

	private async Task<DocumentRecord?> CaptureIntoAsync(Func<DocumentRecord> document)
	{
		if (!MediaPicker.Default.IsCaptureSupported)
		{
			await Shell.Current.DisplayAlertAsync("Không có camera", "Thiết bị này không hỗ trợ chụp ảnh.", "OK");
			return null;
		}
		if (!await permissions.EnsureCameraAsync()) return null;

		// The in-app document camera (live outline, automatic capture, several pages); the system camera app only when
		// it cannot start (no usable back camera, CameraX failure...).
		IReadOnlyList<string> photos;
		DocumentCameraResult scan = await scanner.ScanAsync();
		if (scan.Error == null) photos = scan.Photos;
		else
		{
			string? path = await camera.CaptureAsync();
			photos = path == null ? [] : [path];
		}
		if (photos.Count == 0) return null;

		// The camera leaves its JPEGs in our cache; each is deleted once the document has its own copy.
		return await CreateAndStartAsync(document, photos.Select(Source).ToArray());
	}

	/// <summary>Creates the document (or files it into a folder) and writes the placeholder pages, off the UI
	/// thread: both are synchronous disk writes (DocumentStore.Create / Update), and running them on the
	/// calling (UI) thread was blocking the tap handler right up until Shell.Current.GoToAsync, before the
	/// user could see even the "Đang tải..." placeholders the new document is built to show immediately.</summary>
	private Task<DocumentRecord> CreateAndStartAsync(Func<DocumentRecord> document, IReadOnlyList<ImportSource> sources) =>
		Task.Run(() =>
		{
			DocumentRecord doc = document();
			importer.Start(doc.Id, sources);
			return doc;
		});

	private static ImportSource Source(string path) => new(Path.GetFileName(path), _ => Task.FromResult<Stream>(
		new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.DeleteOnClose)));
}
