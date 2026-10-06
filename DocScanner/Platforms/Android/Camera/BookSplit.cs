using Android.Graphics;
using AndroidX.ExifInterface.Media;

namespace DocScanner.Services;

/// <summary>
/// Splits one photo of an open book spread into two upright page photos (left, right), each saved as its own
/// JPEG. Deliberately simple: a straight vertical cut just past the midline, not a spine-aware warp - each half
/// then goes through the exact same single-page pipeline as any other photo (its own crop detection, perspective
/// warp, black &amp; white...), so the two pages end up independently straightened instead of fighting over one
/// perspective transform that can never fit both halves of an open book at once (the actual cause of the "wavy
/// lines" the user reported: ContentAligner and PageBends only ever correct ONE flat/bowed sheet, never a crease
/// between two).
/// </summary>
internal static class BookSplit
{
	// A cap well above the app's ~3508 px (A4 300 DPI) output target: bounds RAM regardless of sensor size,
	// same reasoning as CropPlanner's decode budget for very large photos.
	private const int MaxEdge = 4500;

	// Each half keeps this much past the midline, so a spine that is not perfectly centered (a hand holding the
	// book slightly off, a thick book whose pages do not sit exactly symmetric) still leaves each half's own
	// page boundary fully visible for the normal edge detector to find.
	private const double InnerMargin = 0.08;

	public static (string Left, string Right) SplitInHalf(string sourcePath, string folder)
	{
		Bitmap upright = DecodeUpright(sourcePath);
		try
		{
			int w = upright.Width, h = upright.Height;
			int leftEdge = (int)(w * (0.5 + InnerMargin));
			int rightStart = (int)(w * (0.5 - InnerMargin));

			using Bitmap left = Bitmap.CreateBitmap(upright, 0, 0, leftEdge, h)!;
			using Bitmap right = Bitmap.CreateBitmap(upright, rightStart, 0, w - rightStart, h)!;

			string stamp = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
			string leftPath = System.IO.Path.Combine(folder, $"scan_{stamp}_L.jpg");
			string rightPath = System.IO.Path.Combine(folder, $"scan_{stamp}_R.jpg");
			SaveJpeg(left, leftPath);
			SaveJpeg(right, rightPath);
			return (leftPath, rightPath);
		}
		finally
		{
			upright.Recycle();
		}
	}

	/// <summary>Decoded at a bounded size and rotated upright by EXIF, so the halves need no orientation tag of
	/// their own (and the split cuts along the picture's true left/right, not the sensor's raw axis).</summary>
	private static Bitmap DecodeUpright(string path)
	{
		var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
		BitmapFactory.DecodeFile(path, bounds);
		int sample = 1;
		while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > MaxEdge) sample *= 2;

		var opts = new BitmapFactory.Options { InSampleSize = sample };
		Bitmap decoded = BitmapFactory.DecodeFile(path, opts) ?? throw new IOException("Không đọc được ảnh vừa chụp.");

		int rotation = new ExifInterface(path).RotationDegrees;
		if (rotation == 0) return decoded;
		var m = new Matrix();
		m.PostRotate(rotation);
		Bitmap upright = Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, m, true)!;
		decoded.Recycle();
		return upright;
	}

	/// <summary>Same fast path as AndroidImageService.SaveJpeg: straight into a Java file stream (buffered),
	/// instead of Bitmap.Compress calling back into a managed stream for every few KB.</summary>
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
		if (!ok) throw new IOException("Không ghi được ảnh đã tách.");
	}
}
