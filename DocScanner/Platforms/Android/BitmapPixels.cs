using System.Runtime.InteropServices;
using Android.Graphics;
using Android.Runtime;
using ImageCoreService;

namespace DocScanner.Services;

/// <summary>
/// Moves pixels between an Android <see cref="Bitmap"/> and an <see cref="RgbImage"/> through the bitmap's own
/// memory (NDK jnigraphics, AndroidBitmap_lockPixels), instead of Bitmap.GetPixels / CreateBitmap(int[]): those
/// copy every pixel into a Java int[] and again into a managed int[] (2 x 64 MB for a 16 MP region), which costs
/// time and, on a 3 GB phone, can decide between a render and an OutOfMemoryError. Bitmaps in any other format
/// than RGBA_8888 fall back to the copying calls.
/// </summary>
internal static unsafe class BitmapPixels
{
	private const int FormatRgba8888 = 1; // ANDROID_BITMAP_FORMAT_RGBA_8888

	[StructLayout(LayoutKind.Sequential)]
	private struct AndroidBitmapInfo
	{
		public uint Width, Height, Stride;
		public int Format;
		public uint Flags;
	}

	[DllImport("jnigraphics")]
	private static extern int AndroidBitmap_getInfo(IntPtr env, IntPtr bitmap, out AndroidBitmapInfo info);

	[DllImport("jnigraphics")]
	private static extern int AndroidBitmap_lockPixels(IntPtr env, IntPtr bitmap, out IntPtr pixels);

	[DllImport("jnigraphics")]
	private static extern int AndroidBitmap_unlockPixels(IntPtr env, IntPtr bitmap);

	/// <summary>The bitmap's pixels as RGB (alpha dropped: decoded photos are opaque).</summary>
	public static RgbImage ToRgb(Bitmap bitmap)
	{
		int w = bitmap.Width, h = bitmap.Height;
		IntPtr env = JNIEnv.Handle;
		if (AndroidBitmap_getInfo(env, bitmap.Handle, out AndroidBitmapInfo info) != 0 || info.Format != FormatRgba8888
		    || AndroidBitmap_lockPixels(env, bitmap.Handle, out IntPtr pixels) != 0)
		{
			var argb = new int[w * h];
			bitmap.GetPixels(argb, 0, w, 0, 0, w, h);
			return RgbImage.FromArgb(argb, w, h);
		}
		try
		{
			var img = new RgbImage(w, h);
			byte* src = (byte*)pixels;
			int stride = (int)info.Stride;
			byte[] dst = img.Data;
			Parallel.For(0, h, y =>
			{
				byte* s = src + (long)y * stride; // memory order R, G, B, A
				int o = y * w * 3;
				for (int x = 0; x < w; x++, s += 4, o += 3)
				{
					dst[o] = s[0];
					dst[o + 1] = s[1];
					dst[o + 2] = s[2];
				}
			});
			return img;
		}
		finally
		{
			AndroidBitmap_unlockPixels(env, bitmap.Handle);
		}
	}

	/// <summary>A new opaque ARGB_8888 bitmap holding the gray image (for display: no intermediate RGB copy).</summary>
	public static Bitmap FromGray(GrayImage image)
	{
		Bitmap bitmap = Bitmap.CreateBitmap(image.Width, image.Height, Bitmap.Config.Argb8888!)!;
		WriteGray(bitmap, image);
		return bitmap;
	}

	/// <summary>A new opaque ARGB_8888 bitmap holding the image.</summary>
	public static Bitmap FromRgb(RgbImage image)
	{
		Bitmap bitmap = Bitmap.CreateBitmap(image.Width, image.Height, Bitmap.Config.Argb8888!)!;
		WriteRgb(bitmap, image);
		return bitmap;
	}

	/// <summary>Overwrites an existing ARGB_8888 bitmap of the same size with the gray image: a screen that shows a new
	/// picture on every slider step reuses its bitmaps instead of allocating ~9 MB (and a GC) per frame.</summary>
	public static void WriteGray(Bitmap bitmap, GrayImage image)
	{
		int w = image.Width, h = image.Height;
		IntPtr env = JNIEnv.Handle;
		if (AndroidBitmap_getInfo(env, bitmap.Handle, out AndroidBitmapInfo info) != 0 || info.Format != FormatRgba8888
		    || AndroidBitmap_lockPixels(env, bitmap.Handle, out IntPtr pixels) != 0)
		{
			var argb = new int[w * h];
			byte[] g = image.Data;
			for (int i = 0; i < argb.Length; i++) argb[i] = unchecked((int)0xFF000000) | (g[i] * 0x010101);
			bitmap.SetPixels(argb, 0, w, 0, 0, w, h);
			return;
		}
		try
		{
			byte* dstBase = (byte*)pixels;
			int stride = (int)info.Stride;
			byte[] src = image.Data;
			Parallel.For(0, h, y =>
			{
				uint* d = (uint*)(dstBase + (long)y * stride);
				for (int x = 0, o = y * w; x < w; x++, o++)
					d[x] = 0xFF000000u | (uint)(src[o] * 0x010101); // memory order R, G, B, A = little-endian ABGR
			});
		}
		finally
		{
			AndroidBitmap_unlockPixels(env, bitmap.Handle); // also tells the view the pixels changed
		}
	}

	/// <summary>Overwrites an existing ARGB_8888 bitmap of the same size with the image (see <see cref="WriteGray"/>).</summary>
	public static void WriteRgb(Bitmap bitmap, RgbImage image)
	{
		int w = image.Width, h = image.Height;
		IntPtr env = JNIEnv.Handle;
		if (AndroidBitmap_getInfo(env, bitmap.Handle, out AndroidBitmapInfo info) != 0 || info.Format != FormatRgba8888
		    || AndroidBitmap_lockPixels(env, bitmap.Handle, out IntPtr pixels) != 0)
		{
			var argb = new int[w * h];
			byte[] data = image.Data;
			Parallel.For(0, h, y =>
			{
				for (int x = 0, o = y * w * 3, c = y * w; x < w; x++, o += 3, c++)
					argb[c] = unchecked((int)0xFF000000) | (data[o] << 16) | (data[o + 1] << 8) | data[o + 2];
			});
			bitmap.SetPixels(argb, 0, w, 0, 0, w, h);
			return;
		}
		try
		{
			byte* dstBase = (byte*)pixels;
			int stride = (int)info.Stride;
			byte[] src = image.Data;
			Parallel.For(0, h, y =>
			{
				byte* d = dstBase + (long)y * stride;
				int o = y * w * 3;
				for (int x = 0; x < w; x++, d += 4, o += 3)
				{
					d[0] = src[o];
					d[1] = src[o + 1];
					d[2] = src[o + 2];
					d[3] = 255;
				}
			});
		}
		finally
		{
			AndroidBitmap_unlockPixels(env, bitmap.Handle);
		}
	}
}
