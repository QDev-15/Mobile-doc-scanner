using DocScanner.Core;
using DocScanner.ViewModels;
using ImageCoreService;
#if ANDROID
using Android.Graphics;
using DocScanner.Services;
#endif

namespace DocScanner.Views;

/// <summary>
/// The result screen. The picture is not bound through an ImageSource (that would encode and decode a file for every
/// change): the view model's preview goes straight into a bitmap on the native ImageView, and brightness / contrast
/// are a ColorMatrixColorFilter on that view, which the GPU applies while drawing. Moving a slider therefore costs no
/// pixel work at all, however fast the thumb moves.
/// </summary>
public partial class ResultPage : ContentPage
{
	private readonly ResultViewModel _viewModel;

	public ResultPage(ResultViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = _viewModel = viewModel;
		_viewModel.PreviewChanged += OnPreviewChanged;
		_viewModel.ToneChanged += OnToneChanged;
		_viewModel.FilterThumbsChanged += OnFilterThumbsChanged;
		_viewModel.RotationStarted += OnRotationStarted;
		PreviewImage.HandlerChanged += (_, _) => ApplyPending();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
#if ANDROID
		_disposed = false;
#endif
		_viewModel.Attach();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_viewModel.Detach();
#if ANDROID
		DisposeBitmaps();
#endif
	}

#if ANDROID
	private int _frameNumber;
	private bool _disposed; // true from DisposeBitmaps() until the next OnAppearing re-attaches
	private Bitmap? _shown;
	private Bitmap? _pendingBitmap;
	private bool _hasPendingBitmap;
	private ToneAdjust _tone = ToneAdjust.None;

	/// <summary>Spare bitmaps of the current frame size. A slider step produces a new picture; writing it into a spare
	/// bitmap instead of a new one avoids ~9 MB of Java heap and a garbage collection per step, which is what made
	/// dragging stutter.</summary>
	private readonly List<Bitmap> _spare = [];
	private readonly object _spareLock = new();
	private const int MaxSpare = 2;

	/// <summary>A quarter turn is being animated: frames wait for its end (see <see cref="OnRotationStarted"/>).</summary>
	private bool _turning;
	private bool _resetTransformOnNextFrame;
	private Bitmap? _heldDuringTurn;

	private Android.Widget.ImageView? NativeView => PreviewImage.Handler?.PlatformView as Android.Widget.ImageView;

	/// <summary>Pinch / pan / double-tap zoom of the picture, and swipe to the next / previous page at fit.</summary>
	private ZoomController? _zoom;

	private void EnsureZoom()
	{
		if (_zoom != null || NativeView is not { } view) return;
		_zoom = new ZoomController(view) { Swipe = delta => _viewModel.GoCommand.Execute(delta) };
	}

	private void OnPreviewChanged(PreviewFrame? frame)
	{
		int number = ++_frameNumber;
		if (frame == null)
		{
			Show(null);
			return;
		}
		// The pixels (a 2 MP copy) are written off the UI thread, into a spare bitmap; only the newest frame is shown.
		int w = frame.Color?.Width ?? frame.Gray!.Width, h = frame.Color?.Height ?? frame.Gray!.Height;
		_ = Task.Run(() =>
			{
				Bitmap bitmap = Rent(w, h);
				if (frame.Color != null) BitmapPixels.WriteRgb(bitmap, frame.Color);
				else BitmapPixels.WriteGray(bitmap, frame.Gray!);
				return bitmap;
			})
			.ContinueWith(t =>
			{
				if (t.IsFaulted) return;
				MainThread.BeginInvokeOnMainThread(() =>
				{
					// The view model's own render loop (RunLookAsync) is not cancelled by Detach() and can still
					// invoke PreviewChanged after OnDisappearing -> DisposeBitmaps() already ran: without this check
					// the stale bitmap would be reattached (Show) or pooled back into _spare (GiveBack), undoing the
					// native-OOM fix DisposeBitmaps exists for. Dispose it outright instead once the page is torn down.
					if (_disposed) { t.Result.Dispose(); return; }
					if (number == _frameNumber) Show(t.Result);
					else GiveBack(t.Result);
				});
			});
	}

	/// <summary>Releases every native-backed Bitmap this page holds (each ~9 MB uncompressed): without this, revisiting
	/// ResultPage across a session accumulates them faster than the .NET GC (which doesn't see the Java-heap cost at
	/// all) would ever reclaim them, risking a native OOM well before any .NET memory pressure triggers a collection.
	/// Clears each ImageView's own reference first (same ordering OnPreviewChanged/Show already rely on elsewhere in
	/// this file): a system-triggered redraw landing in the gap between OnDisappearing and the next OnAppearing's
	/// fresh Attach()/Refresh() must never hit a disposed bitmap still attached to a view.</summary>
	private void DisposeBitmaps()
	{
		_disposed = true; // see the ContinueWith callbacks in OnPreviewChanged / OnFilterThumbsChanged
		NativeView?.SetImageBitmap(null);
		_shown?.Dispose(); _shown = null;
		_pendingBitmap?.Dispose(); _pendingBitmap = null;
		_hasPendingBitmap = false;
		_heldDuringTurn?.Dispose(); _heldDuringTurn = null;
		lock (_spareLock)
		{
			foreach (Bitmap b in _spare) b.Dispose();
			_spare.Clear();
		}
		foreach (Image thumb in new[] { ThumbColor, ThumbGray, ThumbBlackWhite })
			if (thumb.Handler?.PlatformView is Android.Widget.ImageView view) view.SetImageBitmap(null);
		for (int i = 0; i < _thumbs.Length; i++) { _thumbs[i]?.Dispose(); _thumbs[i] = null; }
	}

	private Bitmap Rent(int w, int h)
	{
		lock (_spareLock)
		{
			int i = _spare.FindIndex(b => b.Width == w && b.Height == h);
			if (i >= 0)
			{
				Bitmap b = _spare[i];
				_spare.RemoveAt(i);
				return b;
			}
		}
		return Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
	}

	private void GiveBack(Bitmap bitmap)
	{
		lock (_spareLock)
		{
			// Only the current frame size is worth keeping (another page / a turn changes it).
			_spare.RemoveAll(b =>
			{
				if (b.Width == bitmap.Width && b.Height == bitmap.Height) return false;
				b.Dispose();
				return true;
			});
			if (_spare.Count < MaxSpare)
			{
				_spare.Add(bitmap);
				return;
			}
		}
		bitmap.Dispose();
	}

	private void Show(Bitmap? bitmap)
	{
		Android.Widget.ImageView? view = NativeView;
		if (view == null)
		{
			if (_pendingBitmap != null) GiveBack(_pendingBitmap);
			_pendingBitmap = bitmap;
			_hasPendingBitmap = true;
			return;
		}
		if (_turning)
		{
			// Shown when the turn animation ends, in the same step as the transform is reset: no flash of the old picture.
			if (_heldDuringTurn != null) GiveBack(_heldDuringTurn);
			_heldDuringTurn = bitmap;
			return;
		}
		if (_resetTransformOnNextFrame)
		{
			_resetTransformOnNextFrame = false;
			PreviewImage.Rotation = 0;
			PreviewImage.Scale = 1;
		}
		view.SetImageBitmap(bitmap);
		EnsureZoom();
		_zoom?.PictureChanged(); // same size (another look): the zoom stays; another page / a turn: back to fit
		ApplyTone(view);
		// The old bitmap goes back to the spares once this frame is drawn (the view no longer references it).
		Bitmap? old = _shown;
		_shown = bitmap;
		if (old != null) view.Post(() => GiveBack(old));
	}

	/// <summary>Instant feedback for a quarter turn: the shown picture is turned (and rescaled to fit) on the GPU at
	/// once, while the turned preview is prepared; the turned bitmap then replaces it without a visible jump.</summary>
	private async void OnRotationStarted(int degrees)
	{
		if (NativeView == null || _shown == null || _turning || PreviewImage.Width <= 0) return;
		_zoom?.Fit(); // the turn animation works on the whole, fitted picture
		_turning = true;
		double cw = PreviewImage.Width, ch = PreviewImage.Height;
		int iw = _shown.Width, ih = _shown.Height;
		double fitNow = Math.Min(cw / iw, ch / ih), fitTurned = Math.Min(cw / ih, ch / iw);
		try
		{
			await Task.WhenAll(
				PreviewImage.RotateToAsync(degrees, 180, Easing.CubicOut),
				PreviewImage.ScaleToAsync(fitTurned / fitNow, 180, Easing.CubicOut));
		}
		finally
		{
			_turning = false;
			Bitmap? held = _heldDuringTurn;
			_heldDuringTurn = null;
			_resetTransformOnNextFrame = true; // until the turned picture is there, keep showing the turned old one
			if (held != null) Show(held);
		}
	}

	private void OnToneChanged(ToneAdjust tone)
	{
		_tone = tone;
		if (NativeView is { } view) ApplyTone(view);
	}

	private void ApplyTone(Android.Widget.ImageView view)
	{
		if (_tone.IsNeutral) view.ClearColorFilter();
		else view.SetColorFilter(new ColorMatrixColorFilter(_tone.ColorMatrix()));
	}

	private int _thumbsNumber;
	private readonly Bitmap?[] _thumbs = new Bitmap?[3];

	/// <summary>The page in each look on the three filter cards (tiny bitmaps, made off the UI thread).</summary>
	private void OnFilterThumbsChanged(FilterThumbs? thumbs)
	{
		int number = ++_thumbsNumber;
		if (thumbs == null)
		{
			SetThumbs([null, null, null]);
			return;
		}
		_ = Task.Run(() => new Bitmap?[]
			{
				BitmapPixels.FromRgb(thumbs.Color), BitmapPixels.FromGray(thumbs.Gray), BitmapPixels.FromGray(thumbs.BlackWhite),
			})
			.ContinueWith(t =>
			{
				if (t.IsFaulted) return;
				MainThread.BeginInvokeOnMainThread(() =>
				{
					if (_disposed || number != _thumbsNumber) foreach (Bitmap? b in t.Result) b?.Dispose();
					else SetThumbs(t.Result);
				});
			});
	}

	private void SetThumbs(Bitmap?[] bitmaps)
	{
		Image[] targets = [ThumbColor, ThumbGray, ThumbBlackWhite];
		for (int i = 0; i < 3; i++)
		{
			if (targets[i].Handler?.PlatformView is not Android.Widget.ImageView view)
			{
				bitmaps[i]?.Dispose();
				continue;
			}
			view.SetImageBitmap(bitmaps[i]);
			_thumbs[i]?.Dispose();
			_thumbs[i] = bitmaps[i];
		}
	}

	private void ApplyPending()
	{
		if (NativeView is not { } view) return;
		EnsureZoom();
		if (_hasPendingBitmap)
		{
			_hasPendingBitmap = false;
			Bitmap? b = _pendingBitmap;
			_pendingBitmap = null;
			Show(b);
		}
		else
		{
			ApplyTone(view);
		}
	}
#else
	private void OnPreviewChanged(PreviewFrame? frame) { }
	private void OnToneChanged(ToneAdjust tone) { }
	private void OnFilterThumbsChanged(FilterThumbs? thumbs) { }
	private void OnRotationStarted(int degrees) { }
	private void ApplyPending() { }
#endif
}
