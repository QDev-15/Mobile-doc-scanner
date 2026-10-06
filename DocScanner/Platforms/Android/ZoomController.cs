using Android.Views;
using Android.Widget;
using Matrix = Android.Graphics.Matrix;
using View = Android.Views.View;

namespace DocScanner.Services;

/// <summary>
/// Pinch-zoom, pan and double-tap zoom for an Android ImageView (the picture of a MAUI Image), the way photo viewers
/// behave: the picture starts fitted; two fingers zoom around their midpoint (up to <see cref="MaxZoom"/>); one finger
/// moves a zoomed picture, never past its edges; a double tap zooms to 2.5x at the tapped point or back to fit. At fit,
/// a horizontal fling reports <see cref="Swipe"/> (next / previous page) and a tap <see cref="Tap"/>. The picture is
/// drawn through the view's matrix, so zooming costs the GPU nothing extra and the bitmap is never copied.
/// </summary>
internal sealed class ZoomController : Java.Lang.Object, View.IOnTouchListener, ScaleGestureDetector.IOnScaleGestureListener,
	GestureDetector.IOnGestureListener, GestureDetector.IOnDoubleTapListener
{
	public const float MaxZoom = 6f;
	private const float DoubleTapZoom = 2.5f;

	private readonly ImageView _view;
	private readonly ScaleGestureDetector _scale;
	private readonly GestureDetector _gestures;
	private readonly Matrix _matrix = new();
	private float _zoom = 1;       // relative to fit
	private float _left, _top;     // picture's top-left corner in view pixels
	private int _bitmapW, _bitmapH;
	private bool _scaling;

	/// <summary>+1 = next page (fling to the left), -1 = previous; only while the picture is at fit.</summary>
	public Action<int>? Swipe { get; set; }

	/// <summary>A single tap (confirmed: not the first half of a double tap).</summary>
	public Action? Tap { get; set; }

	public bool IsZoomed => _zoom > 1.01f;

	public ZoomController(ImageView view)
	{
		_view = view;
		_scale = new ScaleGestureDetector(view.Context, this);
		_gestures = new GestureDetector(view.Context, this);
		_gestures.SetOnDoubleTapListener(this);
		view.SetScaleType(ImageView.ScaleType.Matrix!);
		view.SetOnTouchListener(this);
		view.LayoutChange += (_, e) =>
		{
			if (e.Right - e.Left != e.OldRight - e.OldLeft || e.Bottom - e.Top != e.OldBottom - e.OldTop) Fit();
		};
	}

	/// <summary>Call after the view got a new picture. A picture of the same size keeps the zoom (a new filter of the same
	/// page), another size starts again at fit (another page, a turn).</summary>
	public void PictureChanged()
	{
		var d = _view.Drawable;
		int w = d?.IntrinsicWidth ?? 0, h = d?.IntrinsicHeight ?? 0;
		if (w == _bitmapW && h == _bitmapH && w > 0)
		{
			Apply();
			return;
		}
		_bitmapW = w;
		_bitmapH = h;
		Fit();
	}

	/// <summary>Back to the whole picture, centered.</summary>
	public void Fit()
	{
		_zoom = 1;
		float s = FitScale;
		_left = (_view.Width - _bitmapW * s) / 2;
		_top = (_view.Height - _bitmapH * s) / 2;
		Apply();
	}

	private float FitScale => _bitmapW <= 0 || _bitmapH <= 0 || _view.Width <= 0 || _view.Height <= 0
		? 1
		: Math.Min((float)_view.Width / _bitmapW, (float)_view.Height / _bitmapH);

	private void ZoomAround(float factor, float fx, float fy)
	{
		float z = Math.Clamp(_zoom * factor, 1f, MaxZoom);
		float k = z / _zoom;
		_left = fx - (fx - _left) * k;
		_top = fy - (fy - _top) * k;
		_zoom = z;
		Apply();
	}

	/// <summary>Keeps the picture on screen: centered along an axis where it is smaller than the view, edge to edge
	/// otherwise; then sets the matrix.</summary>
	private void Apply()
	{
		if (_bitmapW <= 0 || _bitmapH <= 0) { _view.ImageMatrix = _matrix; return; }
		float s = FitScale * _zoom, w = _bitmapW * s, h = _bitmapH * s, vw = _view.Width, vh = _view.Height;
		_left = w <= vw ? (vw - w) / 2 : Math.Clamp(_left, vw - w, 0);
		_top = h <= vh ? (vh - h) / 2 : Math.Clamp(_top, vh - h, 0);
		_matrix.Reset();
		_matrix.PostScale(s, s);
		_matrix.PostTranslate(_left, _top);
		_view.ImageMatrix = _matrix;
		_view.Invalidate();
	}

	public bool OnTouch(View? v, MotionEvent? e)
	{
		if (e == null) return false;
		_scale.OnTouchEvent(e);
		_gestures.OnTouchEvent(e);
		// While zoomed (or two fingers are down) the picture owns the gesture: no scrolling / swiping of what is around it.
		if (IsZoomed || e.PointerCount > 1) v?.Parent?.RequestDisallowInterceptTouchEvent(true);
		return true;
	}

	public bool OnScale(ScaleGestureDetector detector)
	{
		ZoomAround(detector.ScaleFactor, detector.FocusX, detector.FocusY);
		return true;
	}

	public bool OnScaleBegin(ScaleGestureDetector detector) => _scaling = true;

	public void OnScaleEnd(ScaleGestureDetector detector) => _scaling = false;

	public bool OnScroll(MotionEvent? e1, MotionEvent e2, float distanceX, float distanceY)
	{
		if (_scaling || !IsZoomed) return false;
		_left -= distanceX;
		_top -= distanceY;
		Apply();
		return true;
	}

	public bool OnFling(MotionEvent? e1, MotionEvent e2, float velocityX, float velocityY)
	{
		if (IsZoomed || _scaling || e1 == null || Swipe == null) return false;
		float dx = e2.GetX() - e1.GetX(), dy = e2.GetY() - e1.GetY();
		if (Math.Abs(dx) < _view.Width * 0.15f || Math.Abs(dx) < Math.Abs(dy) * 1.5f) return false;
		Swipe(dx < 0 ? 1 : -1);
		return true;
	}

	public bool OnDoubleTap(MotionEvent e)
	{
		if (IsZoomed) Fit();
		else ZoomAround(DoubleTapZoom, e.GetX(), e.GetY());
		return true;
	}

	public bool OnSingleTapConfirmed(MotionEvent e)
	{
		Tap?.Invoke();
		return Tap != null;
	}

	public bool OnDoubleTapEvent(MotionEvent e) => false;
	public bool OnDown(MotionEvent e) => true;
	public void OnShowPress(MotionEvent e) { }
	public bool OnSingleTapUp(MotionEvent e) => false;
	public void OnLongPress(MotionEvent e) { }
}
