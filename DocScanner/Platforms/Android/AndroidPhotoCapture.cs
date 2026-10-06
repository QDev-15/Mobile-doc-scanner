using Android.App;
using Android.Content;
using Android.Provider;
using AndroidXFileProvider = AndroidX.Core.Content.FileProvider;

namespace DocScanner.Services;

/// <summary>Where the system camera app writes the photo (the app's cache only, see Resources/xml/capture_paths.xml).</summary>
[ContentProvider(["btk.docscanner.capture"], Exported = false, GrantUriPermissions = true)]
[MetaData("android.support.FILE_PROVIDER_PATHS", Resource = "@xml/capture_paths")]
public class CaptureFileProvider : AndroidXFileProvider;

/// <summary>
/// One photo from the system camera app (ACTION_IMAGE_CAPTURE), written straight into the app's cache through
/// <see cref="CaptureFileProvider"/>. Replaces MAUI's MediaPicker.CapturePhotoAsync, which on Android 12 and older insists
/// on WRITE_EXTERNAL_STORAGE: the app does not declare it (it needs no shared storage), so that call threw and crashed the
/// app (owner's Note 10+, 2026-09-27). Only the camera permission is needed here.
/// </summary>
public sealed class AndroidPhotoCapture : IPhotoCapture
{
	private const int RequestCode = 0x5044;
	private static TaskCompletionSource<bool>? _pending;

	public async Task<string?> CaptureAsync()
	{
		Activity activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No activity.");
		string folder = Path.Combine(FileSystem.CacheDirectory, "capture");
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, $"camera_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.jpg");

		Android.Net.Uri uri = AndroidXFileProvider.GetUriForFile(activity, "btk.docscanner.capture", new Java.IO.File(path))!;
		var intent = new Intent(MediaStore.ActionImageCapture);
		intent.PutExtra(MediaStore.ExtraOutput, uri);
		intent.AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);

		_pending?.TrySetResult(false);
		var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending = tcs;
		activity.StartActivityForResult(intent, RequestCode);

		bool ok = await tcs.Task;
		if (ok && File.Exists(path) && new FileInfo(path).Length > 0) return path;
		try { File.Delete(path); } catch (IOException) { }
		return null;
	}

	/// <summary>Called by MainActivity.OnActivityResult.</summary>
	public static bool OnActivityResult(int requestCode, Result resultCode)
	{
		if (requestCode != RequestCode) return false;
		TaskCompletionSource<bool>? tcs = _pending;
		_pending = null;
		tcs?.TrySetResult(resultCode == Result.Ok);
		return true;
	}
}
