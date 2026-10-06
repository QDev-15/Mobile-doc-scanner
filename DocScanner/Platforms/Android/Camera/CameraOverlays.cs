using Android.Content;
using Android.Graphics;
using Android.Views;
using ImageCoreService;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using RectF = Android.Graphics.RectF;
using Matrix = Android.Graphics.Matrix;
using Path = Android.Graphics.Path;
using View = Android.Views.View;

namespace DocScanner.Services;

/// <summary>
/// Draws the live outline over the camera preview. The preview is shown whole (fit-center) and the analysed frames have
/// its aspect, so a point normalized to the frame lands on the same spot of the picture. The outline glides toward each
/// new detection (a few frames per second) instead of jumping, and fades out when the sheet is lost.
/// </summary>
internal sealed class OutlineOverlayView : View
{
	private readonly Paint _fill = new(PaintFlags.AntiAlias) { Color = Color.Argb(60, 91, 79, 224) }; // #5B4FE0, matches Colors.xaml's Primary
	private readonly Paint _stroke = new(PaintFlags.AntiAlias) { StrokeWidth = 0, StrokeJoin = Paint.Join.Round };
	private readonly Paint _corner = new(PaintFlags.AntiAlias);
	private readonly Paint _flash = new() { Color = Color.White };
	private readonly Paint _focus = new(PaintFlags.AntiAlias) { Color = Color.White };
	private readonly Path _path = new();
	private readonly float _density;

	private int _frameW = 3, _frameH = 4;
	private double[]? _target;  // normalized corners
	private double[]? _shown;
	/// <summary>"2 trang" mode: draw the detected outline as two side-by-side quads (split at the midline) instead
	/// of one, so it is visually obvious before the shot that each side becomes its own page.</summary>
	public bool TwoPage { get; set; }
	private float _alpha;       // 0..1, fades with the outline
	private bool _ready;        // green: held still / about to be captured
	private long _flashStart = -1;
	private (float X, float Y, long Start)? _focusRing;

	public OutlineOverlayView(Context context) : base(context)
	{
		_density = context.Resources!.DisplayMetrics!.Density;
		_stroke.SetStyle(Paint.Style.Stroke);
		_stroke.StrokeWidth = 3 * _density;
		_focus.SetStyle(Paint.Style.Stroke);
		_focus.StrokeWidth = 2 * _density;
	}

	/// <summary>Where the picture is drawn inside this view (fit-center of the frame aspect).</summary>
	public RectF PictureRect
	{
		get
		{
			float s = Math.Min((float)Width / _frameW, (float)Height / _frameH);
			float w = _frameW * s, h = _frameH * s;
			return new RectF((Width - w) / 2, (Height - h) / 2, (Width + w) / 2, (Height + h) / 2);
		}
	}

	public void SetFrame(int width, int height)
	{
		_frameW = Math.Max(1, width);
		_frameH = Math.Max(1, height);
	}

	public void SetOutline(Quad? outline, bool ready)
	{
		_ready = ready;
		_target = outline?.ToValues();
		if (_target != null && (_shown == null || _alpha <= 0.01f)) _shown = (double[])_target.Clone();
		PostInvalidateOnAnimation();
	}

	/// <summary>White flash over the picture: the shutter fired.</summary>
	public void Flash()
	{
		_flashStart = Android.OS.SystemClock.UptimeMillis();
		PostInvalidateOnAnimation();
	}

	public void ShowFocus(float x, float y)
	{
		_focusRing = (x, y, Android.OS.SystemClock.UptimeMillis());
		PostInvalidateOnAnimation();
	}

	protected override void OnDraw(Canvas canvas)
	{
		base.OnDraw(canvas);
		bool animating = false;
		RectF r = PictureRect;

		// Fade in / out and glide toward the latest detection.
		float targetAlpha = _target != null ? 1 : 0;
		if (Math.Abs(_alpha - targetAlpha) > 0.01f)
		{
			_alpha += (targetAlpha - _alpha) * 0.25f;
			animating = true;
		}
		else _alpha = targetAlpha;
		if (_target != null && _shown != null)
			for (int i = 0; i < 8; i++)
			{
				double d = _target[i] - _shown[i];
				if (Math.Abs(d) > 0.0005) { _shown[i] += d * 0.35; animating = true; }
				else _shown[i] = _target[i];
			}

		if (_shown != null && _alpha > 0.01f)
		{
			(float X, float Y) P(int i) => (r.Left + (float)_shown[2 * i] * r.Width(), r.Top + (float)_shown[2 * i + 1] * r.Height());
			(float X, float Y) Mid((float X, float Y) a, (float X, float Y) b) => ((a.X + b.X) / 2, (a.Y + b.Y) / 2);

			Color line = _ready ? Color.Argb(255, 76, 175, 80) : Color.Argb(255, 91, 79, 224);
			_fill.Color = _ready ? Color.Argb((int)(70 * _alpha), 76, 175, 80) : Color.Argb((int)(55 * _alpha), 91, 79, 224);
			_stroke.Color = Color.Argb((int)(255 * _alpha), line.R, line.G, line.B);
			_corner.Color = Color.Argb((int)(255 * _alpha), 255, 255, 255);

			(float X, float Y) tl = P(0), tr = P(1), br = P(2), bl = P(3);
			if (TwoPage)
			{
				(float X, float Y) midTop = Mid(tl, tr), midBottom = Mid(bl, br);
				DrawQuad(canvas, [tl, midTop, midBottom, bl]);
				DrawQuad(canvas, [midTop, tr, br, midBottom]);
				// The split line itself, a touch brighter so it reads as "cut here" rather than a third outline.
				canvas.DrawLine(midTop.X, midTop.Y, midBottom.X, midBottom.Y, _stroke);
				foreach ((float X, float Y) c in (ReadOnlySpan<(float, float)>)[tl, tr, br, bl, midTop, midBottom])
					canvas.DrawCircle(c.X, c.Y, 5 * _density, _corner);
			}
			else
			{
				DrawQuad(canvas, [tl, tr, br, bl]);
				foreach ((float X, float Y) c in (ReadOnlySpan<(float, float)>)[tl, tr, br, bl])
					canvas.DrawCircle(c.X, c.Y, 5 * _density, _corner);
			}
		}

		long now = Android.OS.SystemClock.UptimeMillis();
		if (_focusRing is { } f)
		{
			long age = now - f.Start;
			if (age < 700)
			{
				_focus.Alpha = (int)(255 * (1 - age / 700f));
				canvas.DrawCircle(f.X, f.Y, (36 - 8 * Math.Min(1, age / 200f)) * _density, _focus);
				animating = true;
			}
			else _focusRing = null;
		}

		if (_flashStart >= 0)
		{
			long age = now - _flashStart;
			if (age < 220)
			{
				_flash.Alpha = (int)(200 * (1 - age / 220f));
				canvas.DrawRect(r, _flash);
				animating = true;
			}
			else _flashStart = -1;
		}

		if (animating) PostInvalidateOnAnimation();
	}

	private void DrawQuad(Canvas canvas, ReadOnlySpan<(float X, float Y)> points)
	{
		_path.Reset();
		for (int i = 0; i < points.Length; i++)
		{
			if (i == 0) _path.MoveTo(points[i].X, points[i].Y); else _path.LineTo(points[i].X, points[i].Y);
		}
		_path.Close();
		canvas.DrawPath(_path, _fill);
		canvas.DrawPath(_path, _stroke);
	}
}

/// <summary>The round shutter button; a ring fills up while the sheet is held still (automatic capture).</summary>
internal sealed class ShutterButton : View
{
	private readonly Paint _ring = new(PaintFlags.AntiAlias) { Color = Color.White };
	private readonly Paint _inner = new(PaintFlags.AntiAlias) { Color = Color.White };
	private readonly Paint _progress = new(PaintFlags.AntiAlias) { Color = Color.Argb(255, 76, 175, 80) };
	private readonly float _density;
	private float _value;
	private bool _busy;

	public ShutterButton(Context context) : base(context)
	{
		_density = context.Resources!.DisplayMetrics!.Density;
		_ring.SetStyle(Paint.Style.Stroke);
		_ring.StrokeWidth = 4 * _density;
		_progress.SetStyle(Paint.Style.Stroke);
		_progress.StrokeWidth = 4 * _density;
		_progress.StrokeCap = Paint.Cap.Round;
		Clickable = true;
		ContentDescription = "Chụp";
	}

	/// <summary>0..1: how far the automatic capture has got.</summary>
	public float Progress
	{
		get => _value;
		set { if (Math.Abs(_value - value) > 0.001f) { _value = value; Invalidate(); } }
	}

	/// <summary>A picture is being taken: the button dims.</summary>
	public bool Busy
	{
		get => _busy;
		set { if (_busy != value) { _busy = value; Invalidate(); } }
	}

	public override bool OnTouchEvent(MotionEvent? e)
	{
		if (e?.Action == MotionEventActions.Down) { ScaleX = ScaleY = 0.92f; }
		else if (e?.Action is MotionEventActions.Up or MotionEventActions.Cancel) { ScaleX = ScaleY = 1f; }
		return base.OnTouchEvent(e);
	}

	protected override void OnDraw(Canvas canvas)
	{
		base.OnDraw(canvas);
		float cx = Width / 2f, cy = Height / 2f, radius = Math.Min(Width, Height) / 2f - 4 * _density;
		canvas.DrawCircle(cx, cy, radius, _ring);
		_inner.Alpha = _busy ? 110 : 255;
		canvas.DrawCircle(cx, cy, radius - 8 * _density, _inner);
		if (_value > 0)
			canvas.DrawArc(new RectF(cx - radius, cy - radius, cx + radius, cy + radius), -90, 360 * _value, false, _progress);
	}
}
