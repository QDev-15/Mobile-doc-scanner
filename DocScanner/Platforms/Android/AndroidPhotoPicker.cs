using Android.App;
using Android.Content;
using Android.OS;
using Android.OS.Ext;
using Android.Provider;
using DocScanner.Core;
using AndroidUri = Android.Net.Uri;

namespace DocScanner.Services;

/// <summary>
/// Picks photos with the system picker and hands back their content URIs only. MAUI's MediaPicker.PickPhotosAsync
/// copies every picked photo into the cache before it returns (FileSystemUtils.EnsurePhysicalPath), on the UI thread:
/// with 100 photos the app froze before any progress could show, each photo was then copied a second time into the
/// document, and the cache copies were never deleted. Here the only copy is the import's, straight from the URI into
/// the page folder, in the background.
///
/// Android 13+ (and 11-12 with the updated photo picker module) get the photo picker (ACTION_PICK_IMAGES, up to its
/// limit, typically 100); older ones the document chooser (ACTION_GET_CONTENT, multiple). The read permission for
/// the URIs lasts while the app's activity lives, which is as long as the background import runs.
/// </summary>
public sealed class AndroidPhotoPicker : IPhotoPicker
{
	private const int RequestCode = 0x5043;
	private static TaskCompletionSource<IReadOnlyList<AndroidUri>>? _pending;

	public async Task<IReadOnlyList<ImportSource>> PickAsync()
	{
		Activity activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No activity.");
		_pending?.TrySetResult([]); // a picker left open by an earlier call
		var tcs = new TaskCompletionSource<IReadOnlyList<AndroidUri>>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending = tcs;
		activity.StartActivityForResult(CreateIntent(), RequestCode);

		IReadOnlyList<AndroidUri> uris = await tcs.Task;
		ContentResolver resolver = Android.App.Application.Context.ContentResolver!;
		return uris.Select((uri, i) => new ImportSource($"photo{i + 1}.jpg", _ => OpenAsync(resolver, uri))
		{
			ResolveName = () => NameOf(resolver, uri),
		}).ToList();
	}

	/// <summary>Called by MainActivity.OnActivityResult.</summary>
	public static bool OnActivityResult(int requestCode, Result resultCode, Intent? data)
	{
		if (requestCode != RequestCode) return false;
		TaskCompletionSource<IReadOnlyList<AndroidUri>>? tcs = _pending;
		_pending = null;
		var uris = new List<AndroidUri>();
		if (resultCode == Result.Ok && data != null)
		{
			if (data.ClipData is { } clip)
			{
				for (int i = 0; i < clip.ItemCount; i++)
					if (clip.GetItemAt(i)?.Uri is { } u) uris.Add(u);
			}
			else if (data.Data is { } single)
			{
				uris.Add(single);
			}
		}
		tcs?.TrySetResult(uris);
		return true;
	}

	private static Intent CreateIntent()
	{
		if (HasPhotoPicker())
		{
#pragma warning disable CA1416 // guarded by HasPhotoPicker
			var pick = new Intent(MediaStore.ActionPickImages);
			pick.SetType("image/*"); // without it the picker also offers videos
			pick.PutExtra(MediaStore.ExtraPickImagesMax, MediaStore.PickImagesMaxLimit);
#pragma warning restore CA1416
			return pick;
		}
		var get = new Intent(Intent.ActionGetContent);
		get.SetType("image/*");
		get.AddCategory(Intent.CategoryOpenable);
		get.PutExtra(Intent.ExtraAllowMultiple, true);
		return get;
	}

	/// <summary>The photo picker exists from Android 13, and on 11-12 once the system's photo picker module was updated
	/// (SDK extension level 2), the same test AndroidX uses.</summary>
	private static bool HasPhotoPicker() =>
		OperatingSystem.IsAndroidVersionAtLeast(33)
		|| (OperatingSystem.IsAndroidVersionAtLeast(30) && SdkExtensions.GetExtensionVersion(30) >= 2);

	/// <summary>The photo as a .NET FileStream over the provider's file descriptor: reads are native (OpenInputStream
	/// crosses JNI for every chunk: measured 7 MB/s on an emulator) and the length is known, for the copy progress.
	/// Falls back to OpenInputStream for providers that cannot hand out a descriptor.</summary>
	private static Task<Stream> OpenAsync(ContentResolver resolver, AndroidUri uri)
	{
		try
		{
			using ParcelFileDescriptor? pfd = resolver.OpenFileDescriptor(uri, "r");
			if (pfd != null)
			{
				var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(pfd.DetachFd(), ownsHandle: true);
				return Task.FromResult<Stream>(new FileStream(handle, FileAccess.Read, 1 << 16));
			}
		}
		catch (Java.Lang.Exception) { }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
		return Task.FromResult(resolver.OpenInputStream(uri) ?? throw new IOException("Không mở được ảnh."));
	}

	/// <summary>Display name ("IMG_2031.jpg"), or a name made from the MIME type; null when neither is known.</summary>
	private static string? NameOf(ContentResolver resolver, AndroidUri uri)
	{
		try
		{
			using var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
			if (cursor != null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } name) return name;
		}
		catch (Java.Lang.Exception) { }

		string? mime = resolver.GetType(uri);
		string? ext = mime == null ? null : Android.Webkit.MimeTypeMap.Singleton?.GetExtensionFromMimeType(mime);
		return ext == null ? null : "photo." + ext;
	}
}
