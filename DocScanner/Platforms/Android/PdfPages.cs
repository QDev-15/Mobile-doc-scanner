using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;

namespace DocScanner.Services;

/// <summary>
/// Pages of a PDF as bitmaps, with Android's own PdfRenderer (built into the OS since Android 5: no library, no licence
/// question). One page is rendered at a time (the renderer allows only one open page), from any thread.
/// </summary>
internal sealed class PdfPages : IDisposable
{
	private readonly object _lock = new();
	private readonly ParcelFileDescriptor _file;
	private readonly PdfRenderer _renderer;

	public PdfPages(string path)
	{
		_file = ParcelFileDescriptor.Open(new Java.IO.File(path), ParcelFileMode.ReadOnly)!;
		_renderer = new PdfRenderer(_file);
	}

	public int Count => _renderer.PageCount;

	/// <summary>Page <paramref name="index"/> on white, its long edge <paramref name="longEdge"/> pixels.</summary>
	public Bitmap Render(int index, int longEdge)
	{
		lock (_lock)
		{
			// Close() explicitly: Dispose() only releases the .NET wrapper, and PdfRenderer refuses to open another page while
			// one is still open ("Current page not closed") - every page after the first came out empty (black) on Android 12.
			PdfRenderer.Page page = _renderer.OpenPage(index);
			try
			{
				double scale = (double)longEdge / Math.Max(page.Width, page.Height);
				int w = Math.Max(1, (int)Math.Round(page.Width * scale)), h = Math.Max(1, (int)Math.Round(page.Height * scale));
				Bitmap bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
				bitmap.EraseColor(Android.Graphics.Color.White);
				page.Render(bitmap, null, null, PdfRenderMode.ForDisplay);
				return bitmap;
			}
			finally
			{
				page.Close();
				page.Dispose();
			}
		}
	}

	/// <summary>Page <paramref name="index"/>'s size in PDF points, without rendering it -- used to lay out the
	/// continuous scroll view (<see cref="PdfScrollView"/>) before any page bitmap is decoded.</summary>
	public (int Width, int Height) Size(int index)
	{
		lock (_lock)
		{
			PdfRenderer.Page page = _renderer.OpenPage(index);
			try { return (page.Width, page.Height); }
			finally
			{
				page.Close();
				page.Dispose();
			}
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			_renderer.Close();
			_file.Close();
		}
	}
}
