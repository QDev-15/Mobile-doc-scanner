using System.Windows.Input;
using ImageCoreService;
using Microsoft.Maui.Graphics.Platform;

namespace DocScanner.Views;

/// <summary>
/// Shows a page and lets the user adjust its paper outline with a finger.
///
///  - Points may leave the picture, into the dark margin around it (up to <see cref="OutsideFraction"/> of
///    the picture's size): a sheet cut by the frame has its real corners off-picture.
///  - Drag a corner (big dots) to move it; drag the small square in the middle of a side to push
///    that whole side; drag inside the outline to move all of it.
///  - While a corner or a side is being dragged a magnifier shows the area under the finger
///    (which the finger itself hides), with the outline drawn inside it.
///  - The outline can never become concave, self-crossing or tiny: a move that would do that is
///    simply not applied, so the handle stops at the limit.
///
/// The image is drawn by the control itself (not a separate Image), so the outline, the handles
/// and the magnifier all share exactly the same coordinates. <see cref="Quad"/> is 8 numbers
/// (TL, TR, BR, BL) normalized to 0..1 of the image; <see cref="EditedCommand"/> receives the new
/// values when a drag ends.
/// </summary>
public sealed class QuadEditor : GraphicsView, IDrawable
{
	public static readonly BindableProperty ImagePathProperty = BindableProperty.Create(
		nameof(ImagePath), typeof(string), typeof(QuadEditor), null, propertyChanged: (b, _, n) => ((QuadEditor)b).LoadImage((string?)n));

	public static readonly BindableProperty QuadProperty = BindableProperty.Create(
		nameof(Quad), typeof(double[]), typeof(QuadEditor), null, propertyChanged: (b, _, n) => ((QuadEditor)b).SetQuad((double[]?)n));

	/// <summary>Curved sides of the detected outline (8 numbers, see ImageCoreService.PageBends), drawn as curves; null =
	/// straight. The page is straightened along these curves, so the screen shows exactly what will be cut.</summary>
	public static readonly BindableProperty BendProperty = BindableProperty.Create(
		nameof(Bend), typeof(double[]), typeof(QuadEditor), null, propertyChanged: (b, _, _) => ((QuadEditor)b).Invalidate());

	public double[]? Bend { get => (double[]?)GetValue(BendProperty); set => SetValue(BendProperty, value); }

	public static readonly BindableProperty DetectedProperty = BindableProperty.Create(
		nameof(Detected), typeof(bool), typeof(QuadEditor), true, propertyChanged: (b, _, _) => ((QuadEditor)b).Invalidate());

	public static readonly BindableProperty ManualProperty = BindableProperty.Create(
		nameof(Manual), typeof(bool), typeof(QuadEditor), false, propertyChanged: (b, _, _) => ((QuadEditor)b).Invalidate());

	public static readonly BindableProperty EditedCommandProperty = BindableProperty.Create(
		nameof(EditedCommand), typeof(ICommand), typeof(QuadEditor), null);

	public static readonly BindableProperty SwipeCommandProperty = BindableProperty.Create(
		nameof(SwipeCommand), typeof(ICommand), typeof(QuadEditor), null);

	/// <summary>Called with +1 (swipe left: next page) or -1 (swipe right: previous page) after a horizontal
	/// fling that started outside the outline (inside it, a drag moves the outline).</summary>
	public ICommand? SwipeCommand { get => (ICommand?)GetValue(SwipeCommandProperty); set => SetValue(SwipeCommandProperty, value); }

	public string? ImagePath { get => (string?)GetValue(ImagePathProperty); set => SetValue(ImagePathProperty, value); }
	public double[]? Quad { get => (double[]?)GetValue(QuadProperty); set => SetValue(QuadProperty, value); }

	/// <summary>False = the outline is only the fallback frame (drawn amber).</summary>
	public bool Detected { get => (bool)GetValue(DetectedProperty); set => SetValue(DetectedProperty, value); }

	/// <summary>True = the user placed the outline (drawn blue).</summary>
	public bool Manual { get => (bool)GetValue(ManualProperty); set => SetValue(ManualProperty, value); }

	/// <summary>Called with the 8 new numbers when the user lets go.</summary>
	public ICommand? EditedCommand { get => (ICommand?)GetValue(EditedCommandProperty); set => SetValue(EditedCommandProperty, value); }

	// All sizes are in dp (the units of both drawing and touch).
	private const float PicturePadding = 34;  // room around the picture: corners at its edge stay reachable and clear of the system back-swipe zone
	private const float HitRadius = 38;       // how close a touch must be to grab a handle
	private const float CornerDot = 11;
	private const float SideHandle = 8;
	private const float LoupeRadius = 64;
	private const float LoupeZoom = 3;
	/// <summary>How far outside the picture a point may go, as a share of the picture's width / height.</summary>
	public const double OutsideFraction = 0.2;

	private const double MinArea = 0.01;      // outline area, as a share of the picture
	private const double MinSide = 0.04;      // shortest side, as a share of the picture's size

	/// <summary>Swipe = a touch that started outside the outline: a horizontal fling turns the page.</summary>
	private enum DragKind { None, Corner, Side, Move, Swipe }

	/// <summary>Horizontal fling needed to turn the page: this share of the view width, and mostly sideways.</summary>
	private const float SwipeFraction = 0.2f;

	private Microsoft.Maui.Graphics.IImage? _image;
	private string? _loadedPath;
	private double[] _q = new double[8];
	private bool _hasQuad;

	private DragKind _drag;
	private int _index;
	private double[] _base = new double[8];
	private PointF _start, _touch;

	public QuadEditor()
	{
		Drawable = this;
		StartInteraction += OnStart;
		DragInteraction += OnDrag;
		EndInteraction += OnEnd;
		CancelInteraction += OnCancel;
	}

	#region Loading / binding

	private async void LoadImage(string? path)
	{
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
		{
			_loadedPath = null;
			_image = null;
			Invalidate();
			return;
		}
		if (path == _loadedPath) return;
		_loadedPath = path;
		try
		{
			Microsoft.Maui.Graphics.IImage? img = await Task.Run(() =>
			{
				using FileStream fs = File.OpenRead(path);
				return PlatformImage.FromStream(fs);
			});
			if (path == _loadedPath) { _image = img; Invalidate(); }
		}
		catch (Exception)
		{
			if (path == _loadedPath) { _image = null; Invalidate(); }
		}
	}

	private void SetQuad(double[]? value)
	{
		if (_drag != DragKind.None) return; // the user is holding it: the bound value catches up when they let go
		_hasQuad = value is { Length: 8 };
		if (_hasQuad) Array.Copy(value!, _q, 8);
		Invalidate();
	}

	#endregion

	#region Geometry

	/// <summary>Where the picture sits inside the control (AspectFit with a margin).</summary>
	private bool TryLayout(out float ox, out float oy, out float w, out float h)
	{
		ox = oy = w = h = 0;
		if (_image == null || Width <= 0 || Height <= 0 || _image.Width <= 0 || _image.Height <= 0) return false;
		float availW = (float)Width - 2 * PicturePadding, availH = (float)Height - 2 * PicturePadding;
		if (availW <= 0 || availH <= 0) return false;
		// Fit the picture PLUS the margin points may be dragged into.
		float span = (float)(1 + 2 * OutsideFraction);
		float scale = Math.Min(availW / (_image.Width * span), availH / (_image.Height * span));
		w = _image.Width * scale;
		h = _image.Height * scale;
		ox = ((float)Width - w) / 2;
		oy = ((float)Height - h) / 2;
		return true;
	}

	private static PointF ToView(double[] q, int i, float ox, float oy, float w, float h) =>
		new(ox + (float)q[i * 2] * w, oy + (float)q[i * 2 + 1] * h);

	private static bool IsValid(double[] q)
	{
		var quad = ImageCoreService.Quad.FromValues(q);
		if (!quad.IsConvex || quad.Area < MinArea) return false;
		PointD[] p = quad.ToArray();
		for (int i = 0; i < 4; i++)
		{
			PointD a = p[i], b = p[(i + 1) % 4];
			if (Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) < MinSide) return false;
		}
		return true;
	}

	private static double ClampRange(double v) => Math.Clamp(v, -OutsideFraction, 1 + OutsideFraction);

	private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

	#endregion

	#region Touch

	private void OnStart(object? sender, TouchEventArgs e)
	{
		if (e.Touches.Length == 0) return;
		PointF t = e.Touches[0];
		if (!_hasQuad || !TryLayout(out float ox, out float oy, out float w, out float h))
		{
			BeginSwipe(t); // nothing to edit yet (page still being prepared): only page turning
			return;
		}

		// Corners first, then the middle of each side, then "inside = move everything".
		int best = -1;
		float bestDist = HitRadius;
		for (int i = 0; i < 4; i++)
		{
			float d = Dist(t, ToView(_q, i, ox, oy, w, h));
			if (d < bestDist) { bestDist = d; best = i; }
		}
		if (best >= 0)
		{
			_drag = DragKind.Corner;
			_index = best;
		}
		else
		{
			bestDist = HitRadius;
			for (int i = 0; i < 4; i++)
			{
				PointF a = ToView(_q, i, ox, oy, w, h), b = ToView(_q, (i + 1) % 4, ox, oy, w, h);
				float d = Dist(t, new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2));
				if (d < bestDist) { bestDist = d; best = i; }
			}
			if (best >= 0)
			{
				_drag = DragKind.Side;
				_index = best;
			}
			else if (ImageCoreService.Quad.FromValues(_q).Contains((t.X - ox) / w, (t.Y - oy) / h))
			{
				_drag = DragKind.Move;
			}
			else
			{
				BeginSwipe(t); // touched outside the outline: maybe a page turn
				return;
			}
		}

		_base = (double[])_q.Clone();
		_start = _touch = t;
		Invalidate();
	}

	private void BeginSwipe(PointF t)
	{
		_drag = DragKind.Swipe;
		_start = _touch = t;
	}

	private void OnDrag(object? sender, TouchEventArgs e)
	{
		if (_drag == DragKind.Swipe)
		{
			if (e.Touches.Length > 0) _touch = e.Touches[0];
			return;
		}
		if (_drag == DragKind.None || e.Touches.Length == 0 || !TryLayout(out float ox, out float oy, out float w, out float h)) return;
		_touch = e.Touches[0];
		float dx = _touch.X - _start.X, dy = _touch.Y - _start.Y;

		double[] candidate = (double[])_base.Clone();
		switch (_drag)
		{
			case DragKind.Corner:
				candidate[_index * 2] = ClampRange(_base[_index * 2] + dx / w);
				candidate[_index * 2 + 1] = ClampRange(_base[_index * 2 + 1] + dy / h);
				break;

			case DragKind.Side:
				MoveSide(candidate, ox, oy, w, h, dx, dy);
				break;

			case DragKind.Move:
			{
				double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
				for (int i = 0; i < 4; i++)
				{
					minX = Math.Min(minX, _base[i * 2]); maxX = Math.Max(maxX, _base[i * 2]);
					minY = Math.Min(minY, _base[i * 2 + 1]); maxY = Math.Max(maxY, _base[i * 2 + 1]);
				}
				double mx = Math.Clamp(dx / w, -OutsideFraction - minX, 1 + OutsideFraction - maxX), my = Math.Clamp(dy / h, -OutsideFraction - minY, 1 + OutsideFraction - maxY);
				for (int i = 0; i < 4; i++) { candidate[i * 2] = _base[i * 2] + mx; candidate[i * 2 + 1] = _base[i * 2 + 1] + my; }
				break;
			}
		}

		// A move that would fold the outline is not applied: the handle just stops at the limit.
		if (IsValid(candidate)) _q = candidate;
		Invalidate();
	}

	private static bool InRange(double v) => v >= -OutsideFraction && v <= 1 + OutsideFraction;

	/// <summary>Pushes a side along its own normal, taking its two corners with it.</summary>
	private void MoveSide(double[] candidate, float ox, float oy, float w, float h, float dx, float dy)
	{
		int i0 = _index, i1 = (_index + 1) % 4;
		PointF b0 = ToView(_base, i0, ox, oy, w, h), b1 = ToView(_base, i1, ox, oy, w, h);
		float ex = b1.X - b0.X, ey = b1.Y - b0.Y, len = MathF.Sqrt(ex * ex + ey * ey);
		if (len < 1) return;
		float nx = -ey / len, ny = ex / len;
		float d = dx * nx + dy * ny; // how far the finger moved along the normal

		// Both corners must stay inside the allowed area: shrink the push until they do.
		for (int step = 0; step < 12; step++)
		{
			double x0 = (b0.X + nx * d - ox) / w, y0 = (b0.Y + ny * d - oy) / h;
			double x1 = (b1.X + nx * d - ox) / w, y1 = (b1.Y + ny * d - oy) / h;
			if (InRange(x0) && InRange(y0) && InRange(x1) && InRange(y1))
			{
				candidate[i0 * 2] = x0; candidate[i0 * 2 + 1] = y0;
				candidate[i1 * 2] = x1; candidate[i1 * 2 + 1] = y1;
				return;
			}
			d *= 0.85f;
		}
	}

	private void OnEnd(object? sender, TouchEventArgs e)
	{
		if (_drag == DragKind.Swipe)
		{
			_drag = DragKind.None;
			if (e.Touches.Length > 0) _touch = e.Touches[0];
			float dx = _touch.X - _start.X, dy = _touch.Y - _start.Y;
			if (Math.Abs(dx) >= Width * SwipeFraction && Math.Abs(dx) > 1.5f * Math.Abs(dy))
				SwipeCommand?.Execute(dx < 0 ? 1 : -1);
			return;
		}
		if (_drag == DragKind.None) return;
		bool moved = !_q.SequenceEqual(_base);
		_drag = DragKind.None;
		Invalidate();
		if (moved) EditedCommand?.Execute(_q.ToArray());
	}

	private void OnCancel(object? sender, EventArgs e)
	{
		if (_drag == DragKind.Swipe) _drag = DragKind.None;
		if (_drag == DragKind.None) return;
		_q = (double[])_base.Clone(); // the system took the touch away (e.g. an edge gesture): undo
		_drag = DragKind.None;
		Invalidate();
	}

	#endregion

	#region Drawing

	/// <summary>The outline as a polygon: the four corners, or with curved sides (<see cref="Bend"/>) 24 points per side.</summary>
	private PointF[] OutlinePoints(PointF[] corners)
	{
		ImageCoreService.PageBends? bends = ImageCoreService.PageBends.FromValues(Bend);
		if (bends is null or { IsFlat: true }) return corners;
		// Only the outline as detected is curved: once the user moves it, it is a plain quadrilateral again.
		if (Quad is not { Length: 8 } || !Quad.AsSpan().SequenceEqual(_q)) return corners;
		var q = new ImageCoreService.Quad(
			new ImageCoreService.PointD(corners[0].X, corners[0].Y), new ImageCoreService.PointD(corners[1].X, corners[1].Y),
			new ImageCoreService.PointD(corners[2].X, corners[2].Y), new ImageCoreService.PointD(corners[3].X, corners[3].Y));
		var points = new List<PointF>(96);
		for (int side = 0; side < 4; side++)
			for (int i = 0; i < 24; i++)
			{
				ImageCoreService.PointD pt = bends.PointOnSide(q, side, i / 24.0);
				points.Add(new PointF((float)pt.X, (float)pt.Y));
			}
		return points.ToArray();
	}

	public void Draw(ICanvas canvas, RectF dirtyRect)
	{
		canvas.FillColor = Color.FromArgb("#101010");
		canvas.FillRectangle(dirtyRect);
		if (!TryLayout(out float ox, out float oy, out float w, out float h) || _image == null) return;

		// The dark margin points can be dragged into, a shade lighter than the background so it reads as "room".
		float padX = (float)OutsideFraction * w, padY = (float)OutsideFraction * h;
		canvas.FillColor = Color.FromArgb("#1E1E1E");
		canvas.FillRectangle(ox - padX, oy - padY, w + 2 * padX, h + 2 * padY);
		canvas.DrawImage(_image, ox, oy, w, h);
		canvas.StrokeColor = Colors.White.WithAlpha(0.35f);
		canvas.StrokeSize = 1;
		canvas.DrawRectangle(ox, oy, w, h);
		if (!_hasQuad) return;

		PointF[] p = Enumerable.Range(0, 4).Select(i => ToView(_q, i, ox, oy, w, h)).ToArray();
		Color colour = Manual ? Colors.DeepSkyBlue : Detected ? Colors.LimeGreen : Colors.Orange;

		// Darken everything outside the outline (even-odd: picture and margin minus the outline).
		PointF[] border = OutlinePoints(p);
		var dim = new PathF();
		dim.AppendRectangle(ox - padX, oy - padY, w + 2 * padX, h + 2 * padY);
		dim.MoveTo(border[0]);
		for (int i = 1; i < border.Length; i++) dim.LineTo(border[i]);
		dim.Close();
		canvas.FillColor = Colors.Black.WithAlpha(0.5f);
		canvas.FillPath(dim, WindingMode.EvenOdd);

		var outline = new PathF();
		outline.MoveTo(border[0]);
		for (int i = 1; i < border.Length; i++) outline.LineTo(border[i]);
		outline.Close();
		canvas.StrokeColor = colour;
		canvas.StrokeSize = 2.5f;
		canvas.DrawPath(outline);

		// Side handles (small squares), then corner dots on top.
		for (int i = 0; i < 4; i++)
		{
			PointF a = p[i], b = p[(i + 1) % 4];
			float mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;
			bool active = _drag == DragKind.Side && _index == i;
			float r = active ? SideHandle * 1.4f : SideHandle;
			canvas.FillColor = Colors.White;
			canvas.FillRoundedRectangle(mx - r, my - r, r * 2, r * 2, 3);
			canvas.StrokeColor = colour;
			canvas.StrokeSize = 2;
			canvas.DrawRoundedRectangle(mx - r, my - r, r * 2, r * 2, 3);
		}
		for (int i = 0; i < 4; i++)
		{
			bool active = _drag == DragKind.Corner && _index == i;
			float r = active ? CornerDot * 1.35f : CornerDot;
			canvas.FillColor = colour;
			canvas.FillCircle(p[i], r);
			canvas.StrokeColor = Colors.White;
			canvas.StrokeSize = 2.5f;
			canvas.DrawCircle(p[i], r);
		}

		if (_drag is DragKind.Corner or DragKind.Side) DrawLoupe(canvas, ox, oy, w, h, p, colour);
	}

	/// <summary>Magnifier for the handle being dragged, floating above the finger (or below it, near the top edge).</summary>
	private void DrawLoupe(ICanvas canvas, float ox, float oy, float w, float h, PointF[] p, Color colour)
	{
		PointF focus = _drag == DragKind.Corner
			? p[_index]
			: new PointF((p[_index].X + p[(_index + 1) % 4].X) / 2, (p[_index].Y + p[(_index + 1) % 4].Y) / 2);

		float lx = Math.Clamp(_touch.X, LoupeRadius + 4, (float)Width - LoupeRadius - 4);
		float ly = _touch.Y - (LoupeRadius + 74);
		if (ly - LoupeRadius < 4) ly = _touch.Y + (LoupeRadius + 74);

		PointF Map(PointF v) => new(lx + (v.X - focus.X) * LoupeZoom, ly + (v.Y - focus.Y) * LoupeZoom);

		canvas.SaveState();
		var circle = new PathF();
		circle.AppendCircle(lx, ly, LoupeRadius);
		canvas.ClipPath(circle);
		canvas.FillColor = Color.FromArgb("#101010");
		canvas.FillRectangle(lx - LoupeRadius, ly - LoupeRadius, LoupeRadius * 2, LoupeRadius * 2);

		PointF topLeft = Map(new PointF(ox, oy));
		canvas.DrawImage(_image!, topLeft.X, topLeft.Y, w * LoupeZoom, h * LoupeZoom);

		var outline = new PathF();
		outline.MoveTo(Map(p[0]));
		for (int i = 1; i < 4; i++) outline.LineTo(Map(p[i]));
		outline.Close();
		canvas.StrokeColor = colour;
		canvas.StrokeSize = 2;
		canvas.DrawPath(outline);

		// Crosshair on the exact spot the handle is at.
		canvas.StrokeColor = Colors.White;
		canvas.StrokeSize = 1.5f;
		canvas.DrawLine(lx - 12, ly, lx + 12, ly);
		canvas.DrawLine(lx, ly - 12, lx, ly + 12);
		canvas.RestoreState();

		canvas.StrokeColor = Colors.White;
		canvas.StrokeSize = 3;
		canvas.DrawCircle(lx, ly, LoupeRadius);
	}

	#endregion
}
