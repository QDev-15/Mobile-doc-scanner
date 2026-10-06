using Android.App;
using Android.Content;

namespace DocScanner.Services;

/// <summary>Starts <see cref="DocumentCameraActivity"/> and waits for its result.</summary>
public sealed class AndroidDocumentCamera : IDocumentCamera
{
	private const int RequestCode = 0x5045; // AndroidPhotoPicker 0x5043, AndroidPhotoCapture 0x5044
	private static TaskCompletionSource<DocumentCameraResult>? _pending;

	public Task<DocumentCameraResult> ScanAsync()
	{
		Activity activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No activity.");
		_pending?.TrySetResult(new DocumentCameraResult([]));
		var tcs = new TaskCompletionSource<DocumentCameraResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending = tcs;
		activity.StartActivityForResult(new Intent(activity, typeof(DocumentCameraActivity)), RequestCode);
		return tcs.Task;
	}

	/// <summary>Called by MainActivity.OnActivityResult.</summary>
	public static bool OnActivityResult(int requestCode, Result resultCode, Intent? data)
	{
		if (requestCode != RequestCode) return false;
		TaskCompletionSource<DocumentCameraResult>? tcs = _pending;
		_pending = null;
		string[] photos = data?.GetStringArrayExtra(DocumentCameraActivity.ExtraPhotos) ?? [];
		string? error = data?.GetStringExtra(DocumentCameraActivity.ExtraError);
		Android.Util.Log.Info("DocScanPerf", $"camera: result {resultCode}, {photos.Length} photo(s), error {error ?? "none"}, waiting {tcs != null}");
		tcs?.TrySetResult(new DocumentCameraResult(
			resultCode == Result.Ok ? photos.Where(File.Exists).ToArray() : [], error));
		return true;
	}
}
