using System.Diagnostics;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.AppCompat.App;
using AndroidX.Camera.Core;
using AndroidX.Camera.Core.ResolutionSelector;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using DocScanner.Core.Camera;
using ImageCoreService;
using Java.Util.Concurrent;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using RectF = Android.Graphics.RectF;
using Matrix = Android.Graphics.Matrix;
using Size = Android.Util.Size;
using View = Android.Views.View;

namespace DocScanner.Services;

/// <summary>
/// The document camera (CameraX): live preview with the sheet outlined as it is found, automatic capture once the sheet
/// is held still, several pages in a row. Analysis frames (~640 x 480, shrunk to 320) run through the
/// live setting of <see cref="DocumentEdgeDetector"/> (whole sheet in view, fewer candidates), on a background thread, only the latest frame (a slow frame is
/// dropped, never queued). Pictures are full-resolution JPEGs (EXIF orientation) written to the app's cache; the caller
/// imports them like any other photo, so the page then gets the precise outline, curved sides and all.
/// </summary>
[Activity(Theme = "@style/Maui.MainTheme.NoActionBar", ScreenOrientation = ScreenOrientation.Portrait, Exported = false,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode)]
public sealed class DocumentCameraActivity : AppCompatActivity
{
	public const string ExtraPhotos = "photos";
	public const string ExtraError = "error";
	private const string AutoPreference = "camera_auto_capture";

	/// <summary>Long edge of the frames given to the detector (its live setting: a few frames a second on a phone).</summary>
	private const int AnalysisEdge = DocumentEdgeDetector.LiveAnalysisEdge;

	/// <summary>Whole sheet (default), or an open book spread auto-split into two page photos right after capture
	/// (<see cref="BookSplit"/>) - each half then runs through the normal single-page pipeline independently, so
	/// the crease between the two pages never has to be corrected by one perspective transform.</summary>
	private enum BookMode { OnePage, TwoPage }

	private PreviewView _preview = null!;
	private OutlineOverlayView _overlay = null!;
	private TextView _hint = null!, _count = null!, _auto = null!, _torch = null!, _done = null!, _onePage = null!, _twoPage = null!;
	private ImageView _thumb = null!;
	private ShutterButton _shutter = null!;
	private Typeface? _icons;

	private ProcessCameraProvider? _provider;
	private ICamera? _camera;
	private ImageCapture? _capture;
	private IExecutorService? _analysisThread;

	private readonly List<string> _photos = [];
	private readonly CaptureStabilizer _stabilizer = new();
	private readonly DocumentEdgeDetector _detector = DocumentEdgeDetector.Live();
	private bool _autoCapture = true, _torchOn, _capturing, _processing, _closing, _finishWhenSaved;
	private BookMode _bookMode = BookMode.OnePage;
	private Quad? _lastOutline;
	private double[]? _lastSignature;

	// Detection timing, logged every 30 frames (tag DocScanPerf).
	private double _detectMs;
	private int _detectCount, _framesSeen;

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		Window!.SetStatusBarColor(Color.Black);
		Window.SetNavigationBarColor(Color.Black);
		Window.AddFlags(WindowManagerFlags.KeepScreenOn);
		try { _icons = Typeface.CreateFromAsset(Assets, "MaterialIcons-Regular.ttf"); } catch (Java.Lang.Exception) { }
		_autoCapture = Microsoft.Maui.Storage.Preferences.Default.Get(AutoPreference, true);

		SetContentView(BuildLayout());
		OnBackPressedDispatcher.AddCallback(this, new BackCallback(this));
		UpdateAutoChip();
		UpdatePageModeChips();
		UpdateCount();

		_deliverFrame = new Java.Lang.Runnable(() =>
		{
			if (Interlocked.Exchange(ref _latestFrame, null) is { } f) OnFrame(f.Detection, f.Signature, f.Width, f.Height, f.Ms);
		});
		_analysisThread = Executors.NewSingleThreadExecutor();
		string captureFolder = System.IO.Path.Combine(CacheDir!.AbsolutePath, "capture");
		Task.Run(() => RemoveLeftovers(captureFolder));
		var future = ProcessCameraProvider.GetInstance(this);
		future.AddListener(new Java.Lang.Runnable(() =>
		{
			try
			{
				_provider = (ProcessCameraProvider)future.Get()!;
				BindCamera();
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
		}), ContextCompat.GetMainExecutor(this));
	}

	protected override void OnDestroy()
	{
		_provider?.UnbindAll();
		_analysisThread?.Shutdown();
		base.OnDestroy();
	}

	#region Layout

	private int Dp(float dp) => (int)Math.Round(dp * Resources!.DisplayMetrics!.Density);

	private View BuildLayout()
	{
		var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
		root.SetBackgroundColor(Color.Black);
		// Android 15+ draws every app edge to edge: keep the bars clear of the status and navigation bars.
		AndroidX.Core.View.ViewCompat.SetOnApplyWindowInsetsListener(root, new SystemBarsPadding());

		// Top bar: close, torch, automatic capture.
		var top = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
		top.SetGravity(GravityFlags.CenterVertical);
		top.SetPadding(Dp(4), Dp(4), Dp(8), Dp(4));
		TextView close = IconButton("", "Đóng"); // close
		close.Click += (_, _) => Close();
		top.AddView(close);
		top.AddView(new View(this), new LinearLayout.LayoutParams(0, 1, 1));
		_torch = IconButton("", "Đèn flash"); // flash_off
		_torch.Click += (_, _) => ToggleTorch();
		top.AddView(_torch);
		_auto = new TextView(this) { Gravity = GravityFlags.Center };
		_auto.SetTextSize(ComplexUnitType.Sp, 13);
		_auto.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
		_auto.Click += (_, _) => { _autoCapture = !_autoCapture; Microsoft.Maui.Storage.Preferences.Default.Set(AutoPreference, _autoCapture); _stabilizer.Reset(); UpdateAutoChip(); };
		top.AddView(_auto, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(8) });
		root.AddView(top, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(56)));

		// Page count: one sheet, or an open book spread (auto-split into two pages after capture).
		var pageRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
		pageRow.SetGravity(GravityFlags.Center);
		_onePage = ModeChip("1 trang");
		_onePage.Click += (_, _) => SetBookMode(BookMode.OnePage);
		pageRow.AddView(_onePage, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(8) });
		_twoPage = ModeChip("2 trang (sách mở)");
		_twoPage.Click += (_, _) => SetBookMode(BookMode.TwoPage);
		pageRow.AddView(_twoPage, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
		root.AddView(pageRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });

		// Picture: preview, outline, hint.
		var middle = new FrameLayout(this);
		_preview = new PreviewView(this);
		_preview.SetScaleType(PreviewView.ScaleType.FitCenter!);
		_preview.Touch += OnPreviewTouch;
		middle.AddView(_preview, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
		_overlay = new OutlineOverlayView(this);
		middle.AddView(_overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
		_hint = new TextView(this) { Gravity = GravityFlags.Center };
		_hint.SetTextColor(Color.White);
		_hint.SetTextSize(ComplexUnitType.Sp, 14);
		_hint.SetPadding(Dp(14), Dp(6), Dp(14), Dp(6));
		_hint.Background = Rounded(Color.Argb(150, 0, 0, 0), 16);
		_hint.Text = "Đang mở camera...";
		middle.AddView(_hint, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
			GravityFlags.Top | GravityFlags.CenterHorizontal) { TopMargin = Dp(12) });
		root.AddView(middle, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

		// Bottom bar: last picture + count, shutter, done.
		var bottom = new FrameLayout(this);
		bottom.SetPadding(Dp(20), Dp(12), Dp(20), Dp(12));
		var thumbBox = new FrameLayout(this);
		_thumb = new ImageView(this);
		_thumb.SetScaleType(ImageView.ScaleType.CenterCrop);
		_thumb.Background = Rounded(Color.Argb(255, 40, 40, 40), 6);
		_thumb.ClipToOutline = true;
		thumbBox.AddView(_thumb, new FrameLayout.LayoutParams(Dp(52), Dp(68), GravityFlags.Center));
		_count = new TextView(this) { Gravity = GravityFlags.Center };
		_count.SetTextColor(Color.White);
		_count.SetTextSize(ComplexUnitType.Sp, 12);
		_count.Background = Rounded(Color.Argb(255, 91, 79, 224), 11);
		_count.SetMinWidth(Dp(22));
		thumbBox.AddView(_count, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(22), GravityFlags.Top | GravityFlags.Right));
		thumbBox.Click += (_, _) => Finish(keep: true);
		bottom.AddView(thumbBox, new FrameLayout.LayoutParams(Dp(64), Dp(80), GravityFlags.CenterVertical | GravityFlags.Left));

		_shutter = new ShutterButton(this);
		_shutter.Click += (_, _) => TakePicture();
		bottom.AddView(_shutter, new FrameLayout.LayoutParams(Dp(76), Dp(76), GravityFlags.Center));

		_done = new TextView(this) { Gravity = GravityFlags.Center, Text = "Xong" };
		_done.SetTextColor(Color.White);
		_done.SetTextSize(ComplexUnitType.Sp, 15);
		_done.SetPadding(Dp(18), Dp(10), Dp(18), Dp(10));
		_done.Background = Rounded(Color.Argb(255, 91, 79, 224), 22);
		_done.Click += (_, _) => Finish(keep: true);
		bottom.AddView(_done, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
			GravityFlags.CenterVertical | GravityFlags.Right));
		root.AddView(bottom, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(112)));
		return root;
	}

	private sealed class SystemBarsPadding : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
	{
		public AndroidX.Core.View.WindowInsetsCompat OnApplyWindowInsets(View v, AndroidX.Core.View.WindowInsetsCompat insets)
		{
			AndroidX.Core.Graphics.Insets bars = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars() | AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout());
			v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
			return AndroidX.Core.View.WindowInsetsCompat.Consumed;
		}
	}

	private TextView IconButton(string glyph, string description)
	{
		var b = new TextView(this) { Gravity = GravityFlags.Center, Text = glyph, ContentDescription = description };
		if (_icons != null) b.Typeface = _icons;
		b.SetTextColor(Color.White);
		b.SetTextSize(ComplexUnitType.Sp, 24);
		b.LayoutParameters = new LinearLayout.LayoutParams(Dp(48), Dp(48));
		return b;
	}

	private Android.Graphics.Drawables.GradientDrawable Rounded(Color color, float radiusDp)
	{
		var d = new Android.Graphics.Drawables.GradientDrawable();
		d.SetColor(color);
		d.SetCornerRadius(Dp(radiusDp));
		return d;
	}

	private void UpdateAutoChip()
	{
		_auto.Text = _autoCapture ? "Tự chụp: Bật" : "Tự chụp: Tắt";
		_auto.SetTextColor(_autoCapture ? Color.White : Color.Argb(255, 200, 200, 200));
		_auto.Background = _autoCapture ? Rounded(Color.Argb(255, 91, 79, 224), 16) : Rounded(Color.Argb(255, 60, 60, 60), 16);
		if (!_autoCapture) _shutter.Progress = 0;
	}

	private TextView ModeChip(string text)
	{
		var b = new TextView(this) { Gravity = GravityFlags.Center, Text = text };
		b.SetTextSize(ComplexUnitType.Sp, 12);
		b.SetPadding(Dp(12), Dp(5), Dp(12), Dp(5));
		return b;
	}

	private void SetBookMode(BookMode mode)
	{
		if (_bookMode == mode) return;
		_bookMode = mode;
		_stabilizer.Reset();
		_overlay.TwoPage = mode == BookMode.TwoPage;
		UpdatePageModeChips();
		if (!_capturing) _hint.Text = DefaultHint();
	}

	private void UpdatePageModeChips()
	{
		void Style(TextView chip, bool active)
		{
			chip.SetTextColor(active ? Color.White : Color.Argb(255, 200, 200, 200));
			chip.Background = Rounded(active ? Color.Argb(255, 91, 79, 224) : Color.Argb(255, 60, 60, 60), 14);
		}
		Style(_onePage, _bookMode == BookMode.OnePage);
		Style(_twoPage, _bookMode == BookMode.TwoPage);
	}

	private string DefaultHint() => _bookMode == BookMode.TwoPage ? "Đưa camera vào cả 2 trang sách" : "Đưa camera vào tờ giấy";

	private void UpdateCount()
	{
		bool any = _photos.Count > 0;
		_count.Text = _photos.Count.ToString();
		_count.Visibility = any ? ViewStates.Visible : ViewStates.Gone;
		_thumb.Visibility = any ? ViewStates.Visible : ViewStates.Invisible;
		_done.Visibility = any ? ViewStates.Visible : ViewStates.Invisible;
	}

	#endregion

	#region Camera

	private void BindCamera()
	{
		if (_provider == null || _closing) return;
		ResolutionSelector Selector(ResolutionStrategy? strategy)
		{
			var b = new ResolutionSelector.Builder().SetAspectRatioStrategy(AspectRatioStrategy.Ratio43FallbackAutoStrategy!);
			if (strategy != null) b.SetResolutionStrategy(strategy);
			return b.Build();
		}

		var preview = new Preview.Builder().SetResolutionSelector(Selector(null)).Build();
		preview.SetSurfaceProvider(ContextCompat.GetMainExecutor(this), _preview.SurfaceProvider);

		var analysis = new ImageAnalysis.Builder()
			.SetResolutionSelector(Selector(new ResolutionStrategy(new Size(640, 480), ResolutionStrategy.FallbackRuleClosestHigherThenLower)))
			.SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
			.SetOutputImageFormat(ImageAnalysis.OutputImageFormatRgba8888)
			.Build();
		analysis.SetAnalyzer(_analysisThread!, new FrameAnalyzer(this));

		_capture = new ImageCapture.Builder()
			.SetCaptureMode(ImageCapture.CaptureModeMaximizeQuality)
			.SetResolutionSelector(Selector(ResolutionStrategy.HighestAvailableStrategy))
			.Build();

		_provider.UnbindAll();
		_camera = _provider.BindToLifecycle(this, CameraSelector.DefaultBackCamera!, preview, analysis, _capture);
		_torch.Visibility = _camera.CameraInfo.HasFlashUnit ? ViewStates.Visible : ViewStates.Invisible;
		_hint.Text = DefaultHint();
	}

	private void ToggleTorch()
	{
		if (_camera == null) return;
		_torchOn = !_torchOn;
		_camera.CameraControl.EnableTorch(_torchOn);
		_torch.Text = _torchOn ? "" : ""; // flash_on / flash_off
	}

	private void OnPreviewTouch(object? sender, View.TouchEventArgs e)
	{
		e.Handled = true;
		if (e.Event?.Action != MotionEventActions.Up || _camera == null) return;
		float x = e.Event.GetX(), y = e.Event.GetY();
		MeteringPoint point = _preview.MeteringPointFactory.CreatePoint(x, y);
		_camera.CameraControl.StartFocusAndMetering(new FocusMeteringAction.Builder(point).Build());
		_overlay.ShowFocus(x, y);
	}

	private sealed class FrameAnalyzer(DocumentCameraActivity owner) : Java.Lang.Object, ImageAnalysis.IAnalyzer
	{
		private int _arrived;

		public void Analyze(IImageProxy image)
		{
			try
			{
				if (owner._capturing || owner._closing) return;
				if (_arrived++ == 0)
					Log.Info("DocScanPerf", $"camera: first frame {image.Width}x{image.Height} format {image.Format} rotation {image.ImageInfo.RotationDegrees}");
				var sw = Stopwatch.StartNew();
				RgbImage frame = ToUprightRgb(image, AnalysisEdge);
				QuadDetection detection = owner._detector.Detect(frame);
				double[] signature = CaptureStabilizer.FrameSignature(frame);
				double ms = sw.Elapsed.TotalMilliseconds;
				int w = frame.Width, h = frame.Height;
				// One Java Runnable for the life of the screen: a new one per frame would be yet another object for the
				// GC bridge to trace.
				owner._latestFrame = new FrameResult(detection, signature, w, h, ms);
				owner._uiHandler.Post(owner._deliverFrame);
			}
			catch (Exception ex)
			{
				Log.Warn("DocScanPerf", "camera frame: " + ex.Message);
			}
			finally
			{
				image.Close();
			}
		}
	}

	/// <summary>An RGBA_8888 analysis frame as an upright RGB picture with the long edge about <paramref name="edge"/>.</summary>
	private static unsafe RgbImage ToUprightRgb(IImageProxy image, int edge)
	{
		var plane = image.GetPlanes()[0];
		int w = image.Width, h = image.Height, rowStride = plane.RowStride, pixelStride = plane.PixelStride;
		byte* src = (byte*)plane.Buffer.GetDirectBufferAddress();
		if (src == null) throw new InvalidOperationException("Frame buffer is not direct.");

		// Sample on a grid (the frame is only ~1.3x the analysis size): box average of the source pixels per target pixel.
		double scale = Math.Min(1.0, (double)edge / Math.Max(w, h));
		int tw = Math.Max(1, (int)Math.Round(w * scale)), th = Math.Max(1, (int)Math.Round(h * scale));
		var rgb = new RgbImage(tw, th);
		byte[] d = rgb.Data;
		for (int y = 0; y < th; y++)
		{
			int y0 = y * h / th, y1 = Math.Max(y0 + 1, (y + 1) * h / th);
			for (int x = 0; x < tw; x++)
			{
				int x0 = x * w / tw, x1 = Math.Max(x0 + 1, (x + 1) * w / tw);
				int r = 0, g = 0, b = 0, n = 0;
				for (int sy = y0; sy < y1; sy++)
				{
					byte* row = src + (long)sy * rowStride;
					for (int sx = x0; sx < x1; sx++)
					{
						byte* p = row + sx * pixelStride;
						r += p[0]; g += p[1]; b += p[2]; n++;
					}
				}
				int o = (y * tw + x) * 3;
				d[o] = (byte)(r / n); d[o + 1] = (byte)(g / n); d[o + 2] = (byte)(b / n);
			}
		}
		int turns = image.ImageInfo.RotationDegrees / 90 % 4;
		return turns == 0 ? rgb : rgb.RotateClockwise(turns);
	}

	private sealed record FrameResult(QuadDetection Detection, double[] Signature, int Width, int Height, double Ms);

	/// <summary>Wide enough to plausibly be two pages side by side rather than one: two A4 sheets together are
	/// ~1.41:1 (width:height); a single page alone is ~0.71:1. 1.1 sits well below the true spread ratio but
	/// above anything a single page could produce even photographed at a slight angle.</summary>
	private const double SpreadMinAspect = 1.1;

	private static bool LooksLikeSpread(Quad q)
	{
		double top = Dist(q.TopLeft, q.TopRight), bottom = Dist(q.BottomLeft, q.BottomRight);
		double left = Dist(q.TopLeft, q.BottomLeft), right = Dist(q.TopRight, q.BottomRight);
		double width = (top + bottom) / 2, height = (left + right) / 2;
		return height > 1e-6 && width / height >= SpreadMinAspect;
	}

	private static double Dist(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

	private readonly Handler _uiHandler = new(Looper.MainLooper!);
	private Java.Lang.Runnable _deliverFrame = null!;
	private FrameResult? _latestFrame;

	private void OnFrame(QuadDetection detection, double[] signature, int width, int height, double ms)
	{
		if (_closing) return;
		_detectMs += ms;
		if (_framesSeen++ < 3) Log.Info("DocScanPerf", $"camera: frame {_framesSeen} analysed in {ms:F0} ms ({width}x{height}, detected {detection.Detected})");
		if (++_detectCount == 30)
		{
			Log.Info("DocScanPerf", $"camera: frame analysis {_detectMs / _detectCount:F0} ms avg ({width}x{height})");
			_detectMs = 0;
			_detectCount = 0;
		}

		Quad? outline = detection.Detected ? detection.Quad : null;
		_lastOutline = outline;
		_lastSignature = signature;
		_overlay.SetFrame(width, height);
		if (_capturing) return;

		// "2 trang": the detector still just finds "a quad" (unchanged) - sometimes that is the whole spread,
		// sometimes (the camera only sees one of the two pages clearly, or glare/shadow favours one side) it is
		// a single page. Splitting a single-page quad down the middle produces two meaningless narrow strips, so
		// in this mode a single-page-shaped quad is treated as "not ready yet", same as finding nothing: the
		// stabilizer never reaches Capture for it, and the hint asks the user to back up.
		bool tooNarrowForSpread = _bookMode == BookMode.TwoPage && outline is { } o && !LooksLikeSpread(o);
		Quad? forStabilizer = tooNarrowForSpread ? null : outline;

		StabilizerState state = _stabilizer.Update(SystemClock.ElapsedRealtime() / 1000.0, forStabilizer, detection.Confidence, signature);
		bool ready = _autoCapture && state.Cue is CaptureCue.Holding or CaptureCue.Capture && state.Progress > 0;
		_overlay.SetOutline(outline, ready);
		_shutter.Progress = _autoCapture && state.Cue == CaptureCue.Holding ? (float)state.Progress : 0;
		_hint.Text = tooNarrowForSpread ? "Lùi máy ra để thấy cả 2 trang sách" : state.Cue switch
		{
			CaptureCue.Searching when outline == null => DefaultHint(),
			CaptureCue.TooFar => "Đưa camera lại gần hơn",
			CaptureCue.OutOfFrame => "Lùi máy ra để thấy cả tờ giấy",
			CaptureCue.Captured => "Đã chụp · đặt trang tiếp theo",
			_ when !_autoCapture => "Nhấn nút để chụp",
			CaptureCue.Holding or CaptureCue.Capture => "Giữ yên máy...",
			_ => DefaultHint(),
		};
		if (_autoCapture && !tooNarrowForSpread && state.Cue == CaptureCue.Capture) TakePicture();
	}

	private void TakePicture()
	{
		if (_capture == null || _capturing || _processing || _closing) return;
		_capturing = true;
		_shutter.Busy = true;
		_shutter.Progress = 0;
		_stabilizer.Captured(_lastOutline, _lastSignature);
		_overlay.Flash();
		_hint.Text = "Đang chụp...";

		string folder = System.IO.Path.Combine(CacheDir!.AbsolutePath, "capture");
		Directory.CreateDirectory(folder);
		string path = System.IO.Path.Combine(folder, $"scan_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.jpg");
		var options = new ImageCapture.OutputFileOptions.Builder(new Java.IO.File(path)).Build();
		_capture.TakePicture(options, ContextCompat.GetMainExecutor(this), new SavedCallback(this, path));
	}

	private void OnSaved(string path)
	{
		_capturing = false;
		_shutter.Busy = false;
		if (_closing) { TryDelete(path); return; }

		if (_bookMode == BookMode.TwoPage)
		{
			_processing = true;
			_hint.Text = "Đang tách 2 trang...";
			string folder = System.IO.Path.GetDirectoryName(path)!;
			Task.Run(() =>
			{
				string? left = null, right = null;
				Exception? error = null;
				try { (left, right) = BookSplit.SplitInHalf(path, folder); TryDelete(path); }
				catch (Exception ex) { error = ex; }

				// _processing must drop (and _photos gain its entries) in the same UI-thread callback: Finish()
				// checks _processing and _photos.Count together, so no window exists where it could see neither.
				RunOnUiThread(() =>
				{
					_processing = false;
					if (_closing)
					{
						if (left != null) TryDelete(left);
						if (right != null) TryDelete(right);
						else TryDelete(path);
						return;
					}
					if (error != null)
					{
						Log.Warn("DocScanPerf", "book split failed: " + error.Message);
						AddPhoto(path); // keep the shot rather than lose it: one page, same as "1 trang"
						return;
					}
					AddPhoto(left!);
					AddPhoto(right!);
				});
			});
			return;
		}

		AddPhoto(path);
	}

	/// <summary>Adds one captured (or split) page: count, thumbnail, "Xong" state.</summary>
	private void AddPhoto(string path)
	{
		_photos.Add(path);
		UpdateCount();
		if (_finishWhenSaved) { Finish(keep: true); return; }
		_hint.Text = "Đã chụp · đặt trang tiếp theo";
		Task.Run(() => LoadThumb(path));
	}

	/// <summary>A small copy of the latest page for the corner thumbnail, decoded off the UI thread.</summary>
	private void LoadThumb(string path)
	{
		var o = new BitmapFactory.Options { InSampleSize = 16 };
		Bitmap? small = BitmapFactory.DecodeFile(path, o);
		if (small == null) return;
		int rotation = new AndroidX.ExifInterface.Media.ExifInterface(path).RotationDegrees;
		if (rotation != 0)
		{
			var m = new Matrix();
			m.PostRotate(rotation);
			Bitmap turned = Bitmap.CreateBitmap(small, 0, 0, small.Width, small.Height, m, true);
			small.Recycle();
			small = turned;
		}
		RunOnUiThread(() => { if (!_closing) _thumb.SetImageBitmap(small); });
	}

	private void OnCaptureFailed(string path, string message)
	{
		_capturing = false;
		_shutter.Busy = false;
		TryDelete(path);
		_stabilizer.Reset();
		Toast.MakeText(this, "Không chụp được ảnh: " + message, ToastLength.Short)!.Show();
	}

	private sealed class SavedCallback(DocumentCameraActivity owner, string path) : Java.Lang.Object, ImageCapture.IOnImageSavedCallback
	{
		public void OnImageSaved(ImageCapture.OutputFileResults results) => owner.OnSaved(path);

		public void OnError(ImageCaptureException exception) => owner.OnCaptureFailed(path, exception.Message ?? "lỗi camera");
	}

	#endregion

	#region Leaving

	private void Close()
	{
		if (_photos.Count == 0) { Finish(keep: false); return; }
		new AndroidX.AppCompat.App.AlertDialog.Builder(this)
			.SetTitle($"Bỏ {_photos.Count} ảnh đã chụp?")!
			.SetMessage("Các ảnh vừa chụp chưa được thêm vào tài liệu.")!
			.SetPositiveButton("Thêm vào tài liệu", (_, _) => Finish(keep: true))!
			.SetNegativeButton("Bỏ ảnh", (_, _) => Finish(keep: false))!
			.SetNeutralButton("Chụp tiếp", (_, _) => { })!
			.Show();
	}

	private void Finish(bool keep)
	{
		if (_closing) return;
		// The picture being taken, or a spread still being split into its two pages, is not in _photos yet: wait for it.
		if (keep && (_capturing || _processing)) { _finishWhenSaved = true; return; }
		_closing = true;
		var data = new Intent();
		if (keep && _photos.Count > 0)
		{
			data.PutExtra(ExtraPhotos, _photos.ToArray());
			SetResult(Result.Ok, data);
		}
		else
		{
			foreach (string p in _photos) TryDelete(p);
			SetResult(Result.Canceled, data);
		}
		Finish();
	}

	private void Fail(Exception ex)
	{
		Log.Warn("DocScanPerf", "camera: " + ex);
		_closing = true;
		foreach (string p in _photos) TryDelete(p);
		var data = new Intent();
		data.PutExtra(ExtraError, ex.Message);
		SetResult(Result.Canceled, data);
		Finish();
	}

	/// <summary>Pictures from a camera session the app never got back (killed while shooting or importing). Each import
	/// deletes its picture once copied, so anything a day old is garbage.</summary>
	private static void RemoveLeftovers(string folder)
	{
		try
		{
			if (!Directory.Exists(folder)) return;
			foreach (string file in Directory.EnumerateFiles(folder, "*.jpg"))
				if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1)) TryDelete(file);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static void TryDelete(string path)
	{
		try { File.Delete(path); } catch (IOException) { }
	}

	private sealed class BackCallback(DocumentCameraActivity owner) : OnBackPressedCallback(true)
	{
		public override void HandleOnBackPressed() => owner.Close();
	}

	#endregion
}
