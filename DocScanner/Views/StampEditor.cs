using DocScanner.Core;
using IImage = Microsoft.Maui.Graphics.IImage;

namespace DocScanner.Views;

/// <summary>A signature on the page being edited: where it is (<see cref="Stamp"/>), its picture and the mask size
/// (which sets its proportions).</summary>
public sealed class StampItem(PageStamp stamp, IImage? picture, int maskWidth, int maskHeight)
{
	public PageStamp Stamp { get; set; } = stamp;
	public IImage? Picture { get; } = picture;
	public int MaskWidth { get; } = maskWidth;
	public int MaskHeight { get; } = maskHeight;
}

/// <summary>
/// The page with its signatures, to place them: tap a signature to select it, drag it to move it, drag the round handle
/// at its corner to resize it (proportions kept), tap × to remove it. Positions are those of <see cref="PageStamp"/>
/// (fractions of the page), so what is shown here is exactly where the render draws them.
/// </summary>
public sealed class StampEditor : GraphicsView, IDrawable
{
	private const float HandleRadius = 13;
	private const double MinSize = 0.06, MaxSize = 1.0;

	private enum Drag { None, Move, Resize }

	private IImage? _page;
	private int _pageW = 1, _pageH = 1;
	private Drag _drag;
	private PointF _start;
	private PageStamp? _startStamp;
	private float _startDistance;

	public List<StampItem> Items { get; } = [];
	public int Selected { get; private set; } = -1;

	/// <summary>A signature was moved, resized or removed.</summary>
	public event Action? Changed;

	public StampEditor()
	{
		Drawable = this;
		StartInteraction += OnStart;
		DragInteraction += OnDrag;
		EndInteraction += (_, _) => _drag = Drag.None;
		CancelInteraction += (_, _) => _drag = Drag.None;
	}

	public void SetPage(IImage? picture, int width, int height)
	{
		_page = picture;
		_pageW = Math.Max(1, width);
		_pageH = Math.Max(1, height);
		Invalidate();
	}

	/// <summary>Adds a signature in the middle of the page and selects it.</summary>
	public void Add(StampItem item)
	{
		Items.Add(item);
		Selected = Items.Count - 1;
		Changed?.Invoke();
		Invalidate();
	}

	public void RemoveSelected()
	{
		if (Selected < 0 || Selected >= Items.Count) return;
		Items.RemoveAt(Selected);
		Selected = -1;
		Changed?.Invoke();
		Invalidate();
	}

	/// <summary>Where the page is drawn in the view (fitted, with a margin).</summary>
	private RectF PageRect()
	{
		float margin = 12, w = (float)Width - 2 * margin, h = (float)Height - 2 * margin;
		if (w <= 0 || h <= 0) return RectF.Zero;
		float s = Math.Min(w / _pageW, h / _pageH);
		return new RectF(margin + (w - _pageW * s) / 2, margin + (h - _pageH * s) / 2, _pageW * s, _pageH * s);
	}

	/// <summary>A signature's footprint on screen (after its quarter turns).</summary>
	private RectF Footprint(StampItem item, RectF page)
	{
		(double left, double top, double w, double h) = Core.Signatures.Stamper.Footprint(item.Stamp, item.MaskWidth, item.MaskHeight, _pageW, _pageH);
		float s = page.Width / _pageW;
		return new RectF(page.X + (float)left * s, page.Y + (float)top * s, (float)w * s, (float)h * s);
	}

	private void OnStart(object? sender, TouchEventArgs e)
	{
		if (e.Touches.Length == 0) return;
		PointF t = e.Touches[0];
		RectF page = PageRect();
		if (Selected >= 0 && Selected < Items.Count)
		{
			RectF f = Footprint(Items[Selected], page);
			if (Distance(t, new PointF(f.Right, f.Top)) <= HandleRadius * 1.6f)
			{
				RemoveSelected(); // the × at the top-right corner
				return;
			}
			if (Distance(t, new PointF(f.Right, f.Bottom)) <= HandleRadius * 1.8f)
			{
				Begin(Drag.Resize, t, Items[Selected].Stamp);
				_startDistance = Math.Max(1, Distance(t, new PointF(f.Center.X, f.Center.Y)));
				return;
			}
		}
		for (int i = Items.Count - 1; i >= 0; i--) // topmost first
		{
			if (!Footprint(Items[i], page).Inflate(8, 8).Contains(t)) continue;
			Selected = i;
			Begin(Drag.Move, t, Items[i].Stamp);
			Invalidate();
			return;
		}
		Selected = -1;
		_drag = Drag.None;
		Invalidate();
	}

	private void Begin(Drag drag, PointF at, PageStamp stamp)
	{
		_drag = drag;
		_start = at;
		_startStamp = stamp;
	}

	private void OnDrag(object? sender, TouchEventArgs e)
	{
		if (_drag == Drag.None || _startStamp == null || Selected < 0 || e.Touches.Length == 0) return;
		PointF t = e.Touches[0];
		RectF page = PageRect();
		if (page.Width <= 0) return;
		StampItem item = Items[Selected];
		if (_drag == Drag.Move)
		{
			double x = _startStamp.CenterX + (t.X - _start.X) / page.Width, y = _startStamp.CenterY + (t.Y - _start.Y) / page.Height;
			item.Stamp = _startStamp with { CenterX = Math.Clamp(x, 0, 1), CenterY = Math.Clamp(y, 0, 1) };
		}
		else
		{
			RectF f = Footprint(item, page);
			float d = Distance(t, new PointF(f.Center.X, f.Center.Y));
			item.Stamp = _startStamp with { Size = Math.Clamp(_startStamp.Size * d / _startDistance, MinSize, MaxSize) };
		}
		Changed?.Invoke();
		Invalidate();
	}

	private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

	public void Draw(ICanvas canvas, RectF dirtyRect)
	{
		RectF page = PageRect();
		if (page.Width <= 0) return;
		canvas.FillColor = Colors.White;
		canvas.FillRectangle(page);
		if (_page != null) canvas.DrawImage(_page, page.X, page.Y, page.Width, page.Height);

		for (int i = 0; i < Items.Count; i++)
		{
			StampItem item = Items[i];
			RectF f = Footprint(item, page);
			if (item.Picture != null)
			{
				canvas.SaveState();
				canvas.Rotate(90 * item.Stamp.Turns, f.Center.X, f.Center.Y);
				// Drawn in the signature's own frame: width and height swap back for a quarter turn.
				float w = item.Stamp.Turns % 2 == 0 ? f.Width : f.Height, h = item.Stamp.Turns % 2 == 0 ? f.Height : f.Width;
				canvas.DrawImage(item.Picture, f.Center.X - w / 2, f.Center.Y - h / 2, w, h);
				canvas.RestoreState();
			}
			if (i != Selected) continue;

			canvas.StrokeColor = Color.FromArgb("#5B4FE0"); // matches Resources/Styles/Colors.xaml's Primary
			canvas.StrokeSize = 1.5f;
			canvas.StrokeDashPattern = [6, 4];
			canvas.DrawRectangle(f);
			canvas.StrokeDashPattern = null;
			// Resize handle (bottom-right) and delete (top-right).
			canvas.FillColor = Color.FromArgb("#5B4FE0");
			canvas.FillCircle(f.Right, f.Bottom, HandleRadius);
			canvas.FillColor = Color.FromArgb("#D32F2F");
			canvas.FillCircle(f.Right, f.Top, HandleRadius);
			canvas.StrokeColor = Colors.White;
			canvas.StrokeSize = 2.2f;
			float c = HandleRadius * 0.42f;
			canvas.DrawLine(f.Right - c, f.Top - c, f.Right + c, f.Top + c);
			canvas.DrawLine(f.Right - c, f.Top + c, f.Right + c, f.Top - c);
			canvas.DrawLine(f.Right - c, f.Bottom + c, f.Right + c, f.Bottom - c); // resize: a diagonal arrow
			canvas.DrawLine(f.Right + c, f.Bottom - c, f.Right + c - 4, f.Bottom - c);
			canvas.DrawLine(f.Right + c, f.Bottom - c, f.Right + c, f.Bottom - c + 4);
		}
	}
}

/// <summary>Draws a signature with a finger: strokes of points in view units (dp), shown in the chosen ink color.</summary>
public sealed class SignaturePad : GraphicsView, IDrawable
{
	public const float PenWidth = 3.2f;

	private readonly List<List<PointF>> _strokes = [];

	public Color Ink { get; set; } = Colors.Black;

	public bool IsEmpty => _strokes.All(s => s.Count == 0);

	public SignaturePad()
	{
		Drawable = this;
		StartInteraction += (_, e) => { if (e.Touches.Length > 0) { _strokes.Add([e.Touches[0]]); Invalidate(); } };
		DragInteraction += (_, e) => { if (e.Touches.Length > 0 && _strokes.Count > 0) { _strokes[^1].Add(e.Touches[0]); Invalidate(); } };
	}

	public void Clear()
	{
		_strokes.Clear();
		Invalidate();
	}

	public IReadOnlyList<IReadOnlyList<ImageCoreService.PointD>> Strokes() =>
		_strokes.Select(s => (IReadOnlyList<ImageCoreService.PointD>)s.Select(p => new ImageCoreService.PointD(p.X, p.Y)).ToList()).ToList();

	public void Draw(ICanvas canvas, RectF dirtyRect)
	{
		canvas.FillColor = Colors.White;
		canvas.FillRectangle(dirtyRect);
		// The line to sign on.
		canvas.StrokeColor = Color.FromArgb("#D0D5DD");
		canvas.StrokeSize = 1;
		canvas.DrawLine(24, (float)Height * 0.72f, (float)Width - 24, (float)Height * 0.72f);

		canvas.StrokeColor = Ink;
		canvas.StrokeSize = PenWidth;
		canvas.StrokeLineCap = LineCap.Round;
		canvas.StrokeLineJoin = LineJoin.Round;
		foreach (List<PointF> s in _strokes)
		{
			if (s.Count == 1)
			{
				canvas.FillColor = Ink;
				canvas.FillCircle(s[0], PenWidth / 2);
				continue;
			}
			var path = new PathF();
			path.MoveTo(s[0]);
			for (int i = 1; i < s.Count; i++) path.LineTo(s[i]);
			canvas.DrawPath(path);
		}
	}
}
