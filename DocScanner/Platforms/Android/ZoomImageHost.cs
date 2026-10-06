using Android.Graphics;
using Android.Widget;

namespace DocScanner.Services;

/// <summary>
/// A MAUI Image used as a zoomable picture viewer (<see cref="ZoomController"/>): pictures are decoded off the UI thread
/// at up to <see cref="MaxEdge"/> pixels (full resolution for an A4 page at 300 DPI, within what the GPU draws in one
/// texture), only the newest request is shown, and the previous bitmap is freed once it is off screen.
/// </summary>
internal sealed class ZoomImageHost
{
	public const int MaxEdge = 4096;

	private readonly Image _image;
	private ZoomController? _zoom;
	private Bitmap? _shown;
	private int _request;

	public ZoomImageHost(Image image) => _image = image;

	/// <summary>+1 / -1: swipe to the next / previous picture (at fit).</summary>
	public Action<int>? Swipe { get; set; }

	private ImageView? View => _image.Handler?.PlatformView as ImageView;

	/// <summary>Shows the picture file (null: nothing).</summary>
	public void ShowFile(string? path) =>
		ShowBitmap(path == null ? null : () => Decode(path, MaxEdge));

	/// <summary>Shows the bitmap <paramref name="make"/> returns (made on a background thread; null: nothing).</summary>
	public async void ShowBitmap(Func<Bitmap?>? make)
	{
		int request = ++_request;
		Bitmap? bitmap = null;
		if (make != null)
		{
			try { bitmap = await Task.Run(make); }
			catch (Exception ex)
			{
				Android.Util.Log.Warn("DocScanPerf", "viewer: picture failed: " + ex);
				bitmap = null;
			}
		}
		if (request != _request)
		{
			bitmap?.Recycle();
			return;
		}
		if (View is not { } view)
		{
			bitmap?.Recycle();
			return;
		}
		_zoom ??= new ZoomController(view) { Swipe = delta => Swipe?.Invoke(delta) };
		view.SetImageBitmap(bitmap);
		_zoom.PictureChanged();
		Bitmap? old = _shown;
		_shown = bitmap;
		// Freed a little later: the render thread may still draw the frame that shows it.
		if (old != null) view.PostDelayed(() => old.Recycle(), 500);
	}

	/// <summary>Frees the picture (the page is going away).</summary>
	public void Clear()
	{
		_request++;
		View?.SetImageBitmap(null);
		_shown?.Recycle();
		_shown = null;
	}

	/// <summary>Decodes a picture file with its long edge at most <paramref name="maxEdge"/> (power-of-two sampling in the
	/// decoder, then an exact downscale when still too big).</summary>
	public static Bitmap? Decode(string path, int maxEdge)
	{
		var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
		BitmapFactory.DecodeFile(path, bounds);
		if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;
		int sample = 1;
		while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= maxEdge) sample *= 2;
		Bitmap? bitmap = BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample });
		if (bitmap == null) return null;
		int longEdge = Math.Max(bitmap.Width, bitmap.Height);
		if (longEdge <= maxEdge) return bitmap;
		double s = (double)maxEdge / longEdge;
		Bitmap scaled = Bitmap.CreateScaledBitmap(bitmap, (int)(bitmap.Width * s), (int)(bitmap.Height * s), true)!;
		bitmap.Recycle();
		return scaled;
	}
}
