using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using RectF = Android.Graphics.RectF;
using View = Android.Views.View;

namespace DocScanner.Services;

/// <summary>
/// A continuous, pinch-zoomable PDF viewer: every page stacked vertically in one scrollable, zoomable canvas --
/// drag up/down to move through pages, pinch or double-tap to zoom into any of them -- the "trình xem PDF hoàn
/// hảo" the owner asked for (2026-10-05), replacing the earlier one-page-at-a-time view with ‹ › buttons.
///
/// A plain native Android View, not a MAUI GraphicsView: <c>QuadEditor</c>/<c>StampEditor</c> draw vector outlines
/// through Microsoft.Maui.Graphics' ICanvas, but every frame here composites several already-decoded page
/// Bitmaps, which the rest of the app always does by manipulating a native Canvas/ImageView directly (see
/// <c>ZoomController</c>, <c>ResultPage</c>) -- going through MAUI's graphics abstraction would mean wrapping each
/// Bitmap as an IImage on every draw for no benefit. <c>PdfViewerPage.xaml.cs</c> hosts it by adding it as the
/// native child of a plain MAUI ContentView, the same "grab the native view and take over" trick
/// <c>ZoomImageHost</c> already uses for the single-picture viewers.
///
/// Memory: a page is decoded once (at <see cref="PageEdge"/>, matching the old single-page viewer's sharpness)
/// and from then on only ever scaled by the Canvas matrix for zoom -- never redecoded. Only the pages touching the
/// viewport (plus one each side) are kept decoded; the rest are recycled, so scrolling through a long document
/// never holds more than a handful of ~24 MB bitmaps at once.
/// </summary>
internal sealed class PdfScrollView : View,
	ScaleGestureDetector.IOnScaleGestureListener, GestureDetector.IOnGestureListener, GestureDetector.IOnDoubleTapListener,
	Java.Lang.IRunnable
{
	public const int PageEdge = 2900;
	private const float MaxZoom = 5f;
	private const float DoubleTapZoom = 2.5f;
	private const float PageGapPx = 24f; // a visible seam between pages, like a real continuous viewer
	private const int KeepAroundVisible = 1; // pages this far past the edge of the viewport stay decoded too

	private readonly ScaleGestureDetector _scaleDetector;
	private readonly GestureDetector _gestureDetector;
	private readonly OverScroller _scroller;
	private readonly Paint _bitmapPaint = new() { FilterBitmap = true, AntiAlias = true };
	private readonly Paint _placeholderPaint = new() { Color = new Color(225, 225, 225) };

	private readonly Dictionary<int, Bitmap> _bitmaps = [];
	private readonly HashSet<int> _loading = [];
	private readonly List<(float W, float H)> _pageSizes = [];
	private readonly List<RectF> _layout = [];

	private PdfPages? _pages;
	private int _generation;
	private float _scale = 1f, _panX, _panY;
	private float _contentHeight;
	private bool _scaling;
	private int _lastReportedPage = -1;

	/// <summary>The topmost visible page changed: (0-based index, total page count).</summary>
	public event Action<int, int>? PageChanged;

	public PdfScrollView(Context context) : base(context)
	{
		SetWillNotDraw(false);
		_scaleDetector = new ScaleGestureDetector(context, this);
		_gestureDetector = new GestureDetector(context, this);
		_gestureDetector.SetOnDoubleTapListener(this);
		_scroller = new OverScroller(context);
	}

	/// <summary>Opens a new document (null: nothing to show). Page sizes are measured one at a time, off the UI
	/// thread (cheap -- open/close, no rendering) and added to the layout as each arrives, so page 1 can appear
	/// and start decoding right away instead of waiting for every page of a long document to be measured first --
	/// "mở cái là xem ngay" (2026-10-05): opening the file must show something immediately, not after a pause.</summary>
	public void SetPages(PdfPages? pages)
	{
		_generation++;
		int gen = _generation;
		EvictAll();
		_pages = pages;
		_pageSizes.Clear();
		_layout.Clear();
		_contentHeight = 0;
		_scale = 1;
		_panX = 0;
		_panY = 0;
		_lastReportedPage = pages is { Count: > 0 } ? 0 : -1;
		PageChanged?.Invoke(Math.Max(0, _lastReportedPage), pages?.Count ?? 0);
		Invalidate();
		if (pages == null || pages.Count == 0) return;

		Task.Run(() =>
		{
			for (int i = 0; i < pages.Count; i++)
			{
				if (gen != _generation) return;
				(int w, int h) = pages.Size(i);
				(float W, float H) size = (Math.Max(1, w), Math.Max(1, h));
				Post(() =>
				{
					if (gen != _generation) return;
					_pageSizes.Add(size);
					AppendLayout();
					Invalidate();
				});
			}
		});
	}

	/// <summary>Frees every decoded page (the document is going away).</summary>
	public void Clear() => SetPages(null);

	protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
	{
		base.OnSizeChanged(w, h, oldw, oldh);
		if (w != oldw && _pageSizes.Count > 0) Relayout();
	}

	/// <summary>Rebuilds every page's rect from scratch against the current viewport width, scale = 1 (the "fit
	/// width" baseline every page is laid out against, whatever its own aspect ratio) -- needed only when the
	/// width itself changes (first layout pass, rotation), since that invalidates every rect already computed.</summary>
	private void Relayout()
	{
		_layout.Clear();
		float w = Width;
		float y = 0;
		foreach ((float pw, float ph) in _pageSizes)
		{
			float h = ph / pw * w;
			_layout.Add(new RectF(0, y, w, y + h));
			y += h + PageGapPx;
		}
		_contentHeight = _layout.Count > 0 ? _layout[^1].Bottom : 0;
		ClampPan();
	}

	/// <summary>Adds the rect for the one page just measured (<see cref="_pageSizes"/>'s new last entry), stacked
	/// right after the previous page -- the progressive counterpart to <see cref="Relayout"/>, so a long
	/// document's later pages don't each cost an O(n) rebuild as their sizes trickle in.</summary>
	private void AppendLayout()
	{
		float w = Width;
		if (w <= 0) return; // not laid out yet; OnSizeChanged -> Relayout() picks up everything measured so far
		(float pw, float ph) = _pageSizes[^1];
		float y = _layout.Count > 0 ? _layout[^1].Bottom + PageGapPx : 0;
		float h = ph / pw * w;
		_layout.Add(new RectF(0, y, w, y + h));
		_contentHeight = y + h;
		ClampPan();
	}

	protected override void OnDraw(Canvas? canvas)
	{
		base.OnDraw(canvas);
		if (canvas == null || _layout.Count == 0) return;

		float contentTop = _panY, contentBottom = _panY + Height / _scale;

		int first = -1, last = -1;
		for (int i = 0; i < _layout.Count; i++)
		{
			RectF r = _layout[i];
			if (r.Bottom < contentTop || r.Top > contentBottom) continue;
			if (first < 0) first = i;
			last = i;
		}
		if (first < 0) return;

		int keepFrom = Math.Max(0, first - KeepAroundVisible), keepTo = Math.Min(_layout.Count - 1, last + KeepAroundVisible);
		EvictOutside(keepFrom, keepTo);

		for (int i = keepFrom; i <= keepTo; i++)
		{
			RectF r = _layout[i];
			float sx = (r.Left - _panX) * _scale, sy = (r.Top - _panY) * _scale;
			float sw = r.Width() * _scale, sh = r.Height() * _scale;
			if (_bitmaps.TryGetValue(i, out Bitmap? bmp))
				canvas.DrawBitmap(bmp, null, new RectF(sx, sy, sx + sw, sy + sh), _bitmapPaint);
			else
			{
				canvas.DrawRect(sx, sy, sx + sw, sy + sh, _placeholderPaint);
				RequestLoad(i);
			}
		}

		if (first != _lastReportedPage)
		{
			_lastReportedPage = first;
			int count = _pages?.Count ?? 0;
			Post(() => PageChanged?.Invoke(first, count));
		}
	}

	private void RequestLoad(int index)
	{
		if (_loading.Contains(index) || _bitmaps.ContainsKey(index) || _pages is not { } pages) return;
		_loading.Add(index);
		int gen = _generation;
		Task.Run(() =>
			{
				try { return pages.Render(index, PageEdge); }
				catch { return null; }
			})
			.ContinueWith(t => Post(() =>
			{
				_loading.Remove(index);
				if (gen != _generation) { t.Result?.Recycle(); return; }
				if (t.Result != null)
				{
					_bitmaps[index] = t.Result;
					Invalidate();
				}
			}));
	}

	private void EvictOutside(int keepFrom, int keepTo)
	{
		List<int>? remove = null;
		foreach (int key in _bitmaps.Keys)
			if (key < keepFrom || key > keepTo) (remove ??= []).Add(key);
		if (remove == null) return;
		foreach (int key in remove)
		{
			_bitmaps[key].Recycle();
			_bitmaps.Remove(key);
		}
	}

	private void EvictAll()
	{
		foreach (Bitmap b in _bitmaps.Values) b.Recycle();
		_bitmaps.Clear();
		_loading.Clear();
	}

	/// <summary>Keeps the viewport inside the document: horizontally only once zoomed past fit-width (every page is
	/// exactly the viewport's width at scale 1, so there is nothing to pan sideways until then).</summary>
	private void ClampPan()
	{
		float viewportW = Width, viewportH = Height;
		float maxPanX = Math.Max(0, viewportW - viewportW / _scale);
		float maxPanY = Math.Max(0, _contentHeight - viewportH / _scale);
		_panX = Math.Clamp(_panX, 0, maxPanX);
		_panY = Math.Clamp(_panY, 0, maxPanY);
	}

	private void ZoomAround(float factor, float focusX, float focusY)
	{
		_scroller.ForceFinished(true);
		float contentX = _panX + focusX / _scale, contentY = _panY + focusY / _scale;
		_scale = Math.Clamp(_scale * factor, 1f, MaxZoom);
		_panX = contentX - focusX / _scale;
		_panY = contentY - focusY / _scale;
		ClampPan();
		Invalidate();
	}

	public override bool OnTouchEvent(MotionEvent? e)
	{
		if (e == null) return false;
		_scaleDetector.OnTouchEvent(e);
		_gestureDetector.OnTouchEvent(e);
		return true;
	}

	public bool OnScale(ScaleGestureDetector detector)
	{
		ZoomAround(detector.ScaleFactor, detector.FocusX, detector.FocusY);
		return true;
	}

	public bool OnScaleBegin(ScaleGestureDetector detector)
	{
		_scaling = true;
		return true;
	}

	public void OnScaleEnd(ScaleGestureDetector detector) => _scaling = false;

	public bool OnDown(MotionEvent e)
	{
		_scroller.ForceFinished(true);
		return true;
	}

	public bool OnScroll(MotionEvent? e1, MotionEvent e2, float distanceX, float distanceY)
	{
		if (_scaling) return false;
		_panX += distanceX / _scale;
		_panY += distanceY / _scale;
		ClampPan();
		Invalidate();
		return true;
	}

	public bool OnFling(MotionEvent? e1, MotionEvent e2, float velocityX, float velocityY)
	{
		if (_scaling) return false;
		float viewportW = Width, viewportH = Height;
		int maxX = (int)Math.Max(0, viewportW - viewportW / _scale);
		int maxY = (int)Math.Max(0, _contentHeight - viewportH / _scale);
		// GestureDetector reports raw finger velocity; OnScroll's distanceX/Y (which _panX/_panY already accumulate
		// directly) are the opposite sign of that, so the fling must be negated to continue in the same direction.
		_scroller.Fling((int)_panX, (int)_panY, (int)(-velocityX / _scale), (int)(-velocityY / _scale), 0, maxX, 0, maxY);
		PostOnAnimation(this);
		return true;
	}

	/// <summary>The fling tick (<see cref="PostOnAnimation"/> only takes an IRunnable, not a plain Action, so the
	/// view implements it directly rather than allocating a wrapper every frame).</summary>
	public void Run()
	{
		if (!_scroller.ComputeScrollOffset()) return;
		_panX = _scroller.CurrX;
		_panY = _scroller.CurrY;
		ClampPan();
		Invalidate();
		if (!_scroller.IsFinished) PostOnAnimation(this);
	}

	public bool OnDoubleTap(MotionEvent e)
	{
		if (_scale > 1.01f) ZoomAround(1f / _scale, e.GetX(), e.GetY());
		else ZoomAround(DoubleTapZoom, e.GetX(), e.GetY());
		return true;
	}

	public bool OnSingleTapConfirmed(MotionEvent e) => false;
	public bool OnDoubleTapEvent(MotionEvent e) => false;
	public void OnShowPress(MotionEvent e) { }
	public bool OnSingleTapUp(MotionEvent e) => false;
	public void OnLongPress(MotionEvent e) { }
}
