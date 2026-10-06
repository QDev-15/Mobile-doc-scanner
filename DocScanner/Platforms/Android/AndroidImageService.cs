using Android.Graphics;
using Android.Media;
using DocScanner.Core;
using ImageCoreService;

namespace DocScanner.Services;

/// <summary>
/// Android decoding for <see cref="IImageService"/>. A 12-48 MP photo is decoded with
/// BitmapFactory.inSampleSize (a power-of-two shrink done inside the decoder), so the full
/// bitmap (up to ~190 MB for 48 MP) never exists in RAM; EXIF rotation is applied to the
/// small bitmap only. The original file is only read.
/// </summary>
public sealed class AndroidImageService : IImageService
{
	private const int JpegQuality = 90;
	private const int MaxDecodeRetries = 3;

	private const int ThumbQuality = 85;

	public Task<ImageInfo> CreateThumbAsync(string originalPath, string thumbPath, int thumbEdge, int userRotationDegrees, CancellationToken ct) =>
		Task.Run(() =>
		{
			(int rawW, int rawH) = ReadSize(originalPath);
			int exif = ReadOrientation(originalPath);
			int orientation = ImageGeometry.ComposeRotation(exif, userRotationDegrees);
			using Bitmap thumb = LoadUpright(originalPath, rawW, rawH, orientation, thumbEdge, ct);
			SaveJpeg(thumb, thumbPath, ThumbQuality);
			return new ImageInfo(rawW, rawH, exif);
		}, ct);

	public Task CreateProxyAsync(string originalPath, string proxyPath, int proxyEdge, int orientation, CancellationToken ct) =>
		Task.Run(() =>
		{
			(int rawW, int rawH) = ReadSize(originalPath);
			using Bitmap proxy = LoadUpright(originalPath, rawW, rawH, orientation, proxyEdge, ct);
			SaveJpeg(proxy, proxyPath, JpegQuality);
		}, ct);

	public Task<RgbImage> LoadRgbAsync(string path, int maxEdge, CancellationToken ct) =>
		Task.Run(() =>
		{
			using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
			BitmapFactory.DecodeFile(path, bounds);
			int rawW = bounds.OutWidth, rawH = bounds.OutHeight;
			if (rawW <= 0 || rawH <= 0) throw new InvalidDataException("Không đọc được ảnh.");

			(int w, int h) = ImageGeometry.FitLongEdge(rawW, rawH, maxEdge);
			using Bitmap decoded = Decode(path, rawW, rawH, maxEdge);
			ct.ThrowIfCancellationRequested();
			using Bitmap scaled = Resize(decoded, w, h);
			return BitmapPixels.ToRgb(scaled);
		}, ct);

	public Task<RgbImage> LoadRegionAsync(string originalPath, int x, int y, int width, int height, int sample, CancellationToken ct) =>
		Task.Run(() =>
		{
			using Bitmap region = DecodeRegion(originalPath, x, y, width, height, sample);
			ct.ThrowIfCancellationRequested();
			return BitmapPixels.ToRgb(region);
		}, ct);

	public Task SaveJpegAsync(RgbImage image, string path, int quality, CancellationToken ct) =>
		Task.Run(() =>
		{
			ct.ThrowIfCancellationRequested();
			using Bitmap bmp = BitmapPixels.FromRgb(image);
			SaveJpeg(bmp, path, quality);
		}, ct);

	/// <summary>Decodes only the rectangle (in the stored pixel grid) at 1/sample scale. Falls back to
	/// decoding the sub-sampled whole picture for formats the region decoder cannot handle, and to a
	/// coarser sample when memory runs out.</summary>
	private static Bitmap DecodeRegion(string path, int x, int y, int w, int h, int sample)
	{
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				using var opts = new BitmapFactory.Options { InSampleSize = sample };
				try
				{
#pragma warning disable CA1422 // the two-argument overload is only deprecated from API 31, which the branch avoids
					using BitmapRegionDecoder? decoder = OperatingSystem.IsAndroidVersionAtLeast(31)
						? BitmapRegionDecoder.NewInstance(path)
						: BitmapRegionDecoder.NewInstance(path, false);
#pragma warning restore CA1422
					Bitmap? region = decoder?.DecodeRegion(new Android.Graphics.Rect(x, y, x + w, y + h), opts);
					if (region != null) return region;
				}
				catch (Java.IO.IOException) { }
				catch (Java.Lang.IllegalArgumentException) { }

				using Bitmap full = BitmapFactory.DecodeFile(path, opts)
					?? throw new InvalidDataException("Không giải mã được ảnh.");
				int rx = x / sample, ry = y / sample;
				int rw = Math.Max(1, Math.Min(full.Width - rx, w / sample));
				int rh = Math.Max(1, Math.Min(full.Height - ry, h / sample));
				return Bitmap.CreateBitmap(full, rx, ry, rw, rh)!;
			}
			catch (Java.Lang.OutOfMemoryError) when (attempt < MaxDecodeRetries)
			{
				sample *= 2;
				GC.Collect();
			}
		}
	}

	private static (int Width, int Height) ReadSize(string path)
	{
		using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
		BitmapFactory.DecodeFile(path, bounds);
		if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
			throw new InvalidDataException("Không đọc được ảnh (định dạng không hỗ trợ).");
		return (bounds.OutWidth, bounds.OutHeight);
	}

	/// <summary>The photo turned upright and shrunk so its long edge is at most <paramref name="maxEdge"/>.
	/// The decoder does the cheap power-of-two shrink (the smaller the target, the faster: a
	/// thumbnail of a 12 MP photo decodes at 1/8 scale); the exact resize and the EXIF rotation
	/// then happen on the small bitmap.</summary>
	private static Bitmap LoadUpright(string path, int rawW, int rawH, int exif, int maxEdge, CancellationToken ct)
	{
		(int upW, int upH) = ImageGeometry.UprightSize(rawW, rawH, exif);
		(int outW, int outH) = ImageGeometry.FitLongEdge(upW, upH, maxEdge);
		ct.ThrowIfCancellationRequested();

		// Each step makes a new bitmap only when it changes something (an upright opaque JPEG decoded straight at the
		// right size goes through untouched): every copy of a 1600 px bitmap is ~10 MB written and a GC later.
		Bitmap current = Decode(path, rawW, rawH, maxEdge);
		try
		{
			ct.ThrowIfCancellationRequested();

			// Resize while still in the stored orientation, then rotate the small result.
			(int scaleW, int scaleH) = ImageGeometry.IsTransposed(exif) ? (outH, outW) : (outW, outH);
			if (current.Width != scaleW || current.Height != scaleH)
				current = Replace(current, Bitmap.CreateScaledBitmap(current, scaleW, scaleH, true)!);
			if (exif != 1)
			{
				using Matrix m = OrientationMatrix(exif);
				current = Replace(current, Bitmap.CreateBitmap(current, 0, 0, current.Width, current.Height, m, true)!);
			}
			if (current.HasAlpha) current = Replace(current, FlattenAlpha(current));
			return current;
		}
		catch
		{
			current.Dispose();
			throw;
		}
	}

	/// <summary>Disposes <paramref name="old"/> and returns <paramref name="next"/> (they are always different bitmaps here).</summary>
	private static Bitmap Replace(Bitmap old, Bitmap next)
	{
		old.Dispose();
		return next;
	}

	private static Bitmap Decode(string path, int rawW, int rawH, int targetEdge)
	{
		int sample = ImageGeometry.DecodeSampleSize(rawW, rawH, targetEdge);
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				using var opts = new BitmapFactory.Options { InSampleSize = sample };
				return BitmapFactory.DecodeFile(path, opts)
					?? throw new InvalidDataException("Không giải mã được ảnh.");
			}
			catch (Java.Lang.OutOfMemoryError) when (attempt < MaxDecodeRetries)
			{
				// Low-memory device: shrink harder instead of crashing. The proxy is resampled
				// from whatever we got, so a coarser decode only costs a little sharpness.
				sample *= 2;
				GC.Collect();
			}
		}
	}

	private static int ReadOrientation(string path)
	{
		try
		{
			using var exif = new ExifInterface(path);
			int o = exif.GetAttributeInt(ExifInterface.TagOrientation, 1);
			return o is >= 1 and <= 8 ? o : 1;
		}
		catch (Java.IO.IOException)
		{
			return 1; // no / broken EXIF: treat as upright
		}
	}

	/// <summary>Always a new bitmap (Android may hand back the source itself for a no-op
	/// resize / rotate), so every caller can dispose both the input and the result.</summary>
	private static Bitmap Resize(Bitmap src, int width, int height) =>
		src.Width == width && src.Height == height
			? src.Copy(src.GetConfig() ?? Bitmap.Config.Argb8888!, false)!
			: Bitmap.CreateScaledBitmap(src, width, height, true)!;

	/// <summary>The transform that turns a picture stored with this EXIF orientation (2..8) upright.</summary>
	private static Matrix OrientationMatrix(int exifOrientation)
	{
		var m = new Matrix();
		switch (exifOrientation)
		{
			case 2: m.PostScale(-1, 1); break;
			case 3: m.PostRotate(180); break;
			case 4: m.PostRotate(180); m.PostScale(-1, 1); break;
			case 5: m.PostRotate(90); m.PostScale(-1, 1); break;
			case 6: m.PostRotate(90); break;
			case 7: m.PostRotate(270); m.PostScale(-1, 1); break;
			case 8: m.PostRotate(270); break;
		}
		return m;
	}

	/// <summary>JPEG has no alpha: a transparent PNG would come out black, so put it on white. Only called for bitmaps
	/// that have alpha.</summary>
	private static Bitmap FlattenAlpha(Bitmap src)
	{
		Bitmap flat = Bitmap.CreateBitmap(src.Width, src.Height, Bitmap.Config.Argb8888!)!;
		using var canvas = new Canvas(flat);
		canvas.DrawColor(Android.Graphics.Color.White);
		canvas.DrawBitmap(src, 0, 0, null);
		return flat;
	}

	/// <summary>The encoder writes straight into a Java file stream (buffered): wrapped in OutputStreamInvoker, the
	/// binding hands the Java stream itself to Bitmap.compress, instead of calling back into a managed FileStream
	/// for every few KB.</summary>
	private static void SaveJpeg(Bitmap bmp, string path, int quality)
	{
		bool ok;
		using (var file = new Java.IO.FileOutputStream(path))
		using (var buffered = new Java.IO.BufferedOutputStream(new Android.Runtime.OutputStreamInvoker(file), 1 << 16))
		using (var stream = new Android.Runtime.OutputStreamInvoker(buffered))
		{
			ok = bmp.Compress(Bitmap.CompressFormat.Jpeg!, quality, stream);
			buffered.Flush();
		}
		if (!ok) throw new IOException("Không ghi được ảnh JPEG.");
	}
}
