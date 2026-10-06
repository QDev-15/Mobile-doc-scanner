using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocScanner.Core;
using DocScanner.Core.Signatures;
using DocScanner.Services;
using ImageCoreService;

namespace DocScanner.ViewModels;

/// <summary>The page in each look, small, for the filter cards.</summary>
public sealed record FilterThumbs(RgbImage Color, GrayImage Gray, GrayImage BlackWhite);

/// <summary>
/// The straightened page, edited live. The screen does not show the saved file: it keeps the straightened page at screen
/// size in memory (<see cref="CropRenderService.RenderPreviewAsync"/>, ~2 MP) and shows every change on it at once:
///  - color / gray / black and white, darkness, background cleaning: re-filtered on that small copy (a fraction of a
///    second; while one is being made, only the latest request is kept);
///  - brightness / contrast: not re-filtered at all, the view applies them as a color matrix on the GPU while the slider
///    moves (<see cref="ToneChanged"/>), with exactly the numbers the saved page gets.
/// The full-size page is saved in the background (<see cref="PageIngestQueue.EnqueueRender"/>) a moment after the last
/// change, and never replaces the picture on screen. Pages not ready yet (still importing / processing) show a message
/// and every control is locked.
/// </summary>
public partial class ResultViewModel(DocumentStore store, PageIngestQueue queue, PageEditService edit, ExportCoordinator exports,
	CropRenderService renderer, SignatureLibrary signatures, SignatureSession signing) : ObservableObject, IQueryAttributable
{
	/// <summary>The signatures drawn on the preview now (compared to notice a change coming back from the signature screen).</summary>
	private string _stampsKey = "";

	/// <summary>The last preview before its signatures were drawn: what the signature screen places them on.</summary>
	private PreviewFrame? _plainFrame;
	/// <summary>Straightened previews kept in memory: this page and its neighbours (swiping back and forth is instant).</summary>
	private const int CachedPreviews = 3;

	/// <summary>Wait this long after the last change before saving the full-size page (sliders are often nudged twice).</summary>
	private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

	private string? _docId;
	private string? _pageId;
	private bool _requested;
	private string? _controlsFor;

	/// <summary>True while the controls are being filled from the page, so their change handlers do not write back.</summary>
	private bool _loading;

	private string? _baseKey;
	private LookPreview? _base;
	private readonly List<(string Key, LookPreview Preview)> _cache = [];
	private bool _computing, _dirty, _prefetching;
	private CancellationTokenSource? _saveDelay;

	/// <summary>A new picture to show (null = nothing yet). Raised on the UI thread.</summary>
	public event Action<PreviewFrame?>? PreviewChanged;

	/// <summary>Brightness / contrast to apply to the shown picture (neutral in black and white). Raised on the UI thread.</summary>
	public event Action<ToneAdjust>? ToneChanged;

	[ObservableProperty]
	private string title = "Kết quả";

	/// <summary>The preview is being made (spinner over the picture).</summary>
	[ObservableProperty]
	private bool isBusy;

	[ObservableProperty]
	private string status = "";

	[ObservableProperty]
	private bool hasError;

	/// <summary>False for a page that is not ready (importing / processing / failed): every control is locked.</summary>
	[ObservableProperty]
	private bool canEdit;

	/// <summary>Current look of the page (drives the three mode buttons).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsColor), nameof(IsGray), nameof(IsBlackWhite), nameof(ShowCleanBackground), nameof(ShowTone))]
	private PageColorMode mode;

	/// <summary>0..100, black-and-white only.</summary>
	[ObservableProperty]
	private double darkness = FilterOptions.DefaultDarkness;

	[ObservableProperty]
	private bool cleanBackground = true;

	/// <summary>-100..100, color and gray.</summary>
	[ObservableProperty]
	private double brightness;

	/// <summary>-100..100, color and gray.</summary>
	[ObservableProperty]
	private double contrast;

	[ObservableProperty]
	private bool canGoPrevious;

	[ObservableProperty]
	private bool canGoNext;

	public bool IsColor => Mode == PageColorMode.Color;
	public bool IsGray => Mode == PageColorMode.Gray;
	public bool IsBlackWhite => Mode == PageColorMode.BlackWhite;
	public bool ShowCleanBackground => Mode != PageColorMode.Color;
	public bool ShowTone => Mode != PageColorMode.BlackWhite;

	/// <summary>Caption of the shape tool: what the page is now (A4 by default, or the outline's own shape).</summary>
	[ObservableProperty]
	private string aspectText = "A4";

	/// <summary>The tool panel open above the bottom bar ("Filters", "Adjust", or "" for none): one at a time, so the
	/// picture keeps most of the screen. Filters are open when the screen opens.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowFilters), nameof(ShowAdjust))]
	private string panel = "Filters";

	public bool ShowFilters => Panel == "Filters";
	public bool ShowAdjust => Panel == "Adjust";

	/// <summary>Opens a panel, or closes it when it is already open.</summary>
	[RelayCommand]
	private void TogglePanel(string name) => Panel = Panel == name ? "" : name;

	/// <summary>"Xong": back to the document (past the outline editor this screen was opened from).</summary>
	[RelayCommand]
	private async Task DoneAsync()
	{
		SaveNow();
		await Shell.Current.GoToAsync("../..");
	}

	private ToneAdjust CurrentTone => new((int)Math.Round(Brightness), (int)Math.Round(Contrast));

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		if (query.TryGetValue("docId", out object? d) && d is string docId
			&& query.TryGetValue("pageId", out object? p) && p is string pageId)
		{
			_docId = docId;
			_pageId = pageId;
			_requested = false;
			_controlsFor = null;
			Refresh();
		}
	}

	public void Attach()
	{
		queue.PageUpdated += OnPageUpdated;
		Refresh();
	}

	public void Detach()
	{
		queue.PageUpdated -= OnPageUpdated;
		SaveNow(); // leaving before the delay ran out: still save the full-size page
	}

	private void OnPageUpdated(PageUpdate update)
	{
		if (update.PageId == _pageId) MainThread.BeginInvokeOnMainThread(Refresh);
	}

	private PageRecord? CurrentPage() =>
		_docId == null || _pageId == null ? null : store.Pages(_docId).FirstOrDefault(x => x.Id == _pageId);

	private void Refresh()
	{
		if (_docId == null || _pageId == null) return;
		IReadOnlyList<PageRecord> pages = store.Pages(_docId);
		PageRecord? page = pages.FirstOrDefault(x => x.Id == _pageId);
		if (page == null)
		{
			Status = "Không tìm thấy trang.";
			CanEdit = false;
			IsBusy = false;
			return;
		}
		int index = pages.ToList().IndexOf(page);
		Title = $"Trang {index + 1}/{pages.Count}";
		CanGoPrevious = index > 0;
		CanGoNext = index < pages.Count - 1;
		AspectText = page.FreeAspect ? "Theo khung" : "Khổ A4";

		// The controls follow the page only when another page is shown: a background save finishing while a slider is
		// being dragged must not snap it back to the saved value.
		if (_controlsFor != page.Id)
		{
			_controlsFor = page.Id;
			_loading = true;
			Mode = page.ColorMode;
			Darkness = page.BwDarkness;
			CleanBackground = page.CleanBackground;
			Brightness = page.Brightness;
			Contrast = page.Contrast;
			_loading = false;
		}

		if (page.State != PageState.Ready)
		{
			CanEdit = false;
			IsBusy = false;
			HasError = false;
			Status = page.State == PageState.Failed
				? "Không đọc được ảnh: " + page.Error
				: "Ảnh đang được tải và xử lý, chờ một chút...";
			ShowBase(null, null);
			return;
		}

		CanEdit = true;
		string key = BaseKey(page);
		if (key != _baseKey) _ = LoadBaseAsync(page.Id, key);
		string stampsKey = StampsKey(page);
		if (stampsKey != _stampsKey)
		{
			_stampsKey = stampsKey;
			if (key == _baseKey) RequestLook(); // signatures added / moved / removed: draw them again
		}
		UpdateStatus(page);
		EnsureSaved(page);
	}

	private void UpdateStatus(PageRecord page)
	{
		if (page.NeedsRender && page.RenderError != null)
		{
			HasError = true;
			Status = "Không lưu được trang: " + page.RenderError;
			return;
		}
		HasError = false;
		Status = page.NeedsRender ? "Đang lưu bản đầy đủ..." : $"{page.CroppedWidth} × {page.CroppedHeight} px";
	}

	/// <summary>Asks the queue for the full-size page once per change (the queue itself skips a page that is current).</summary>
	private void EnsureSaved(PageRecord page)
	{
		if (_docId == null || !page.NeedsRender || page.RenderError != null || _requested || _saveDelay != null) return;
		if (queue.IsPreparing(page.Id)) return;
		_requested = true;
		queue.EnqueueRender(_docId, page.Id);
	}

	#region Preview

	/// <summary>What the straightened preview depends on: the outline, the rotations and the shape (not the look).</summary>
	private string BaseKey(PageRecord p) =>
		$"{_docId}|{p.Id}|{string.Join(',', p.CropQuad ?? [])}|{string.Join(',', p.CropBend ?? [])}|{p.UserRotation}|{p.OutputRotation}|{p.FreeAspect}|{p.RawWidth}x{p.RawHeight}";

	private void ShowBase(string? key, LookPreview? image)
	{
		_baseKey = key;
		_base = image;
		if (image == null) PreviewChanged?.Invoke(null);
		else RequestLook();
		_ = MakeFilterThumbsAsync(key, image?.Page);
		if (image != null) _ = WarmAsync(image);
	}

	/// <summary>Long edge of the pictures on the filter cards.</summary>
	private const int FilterThumbEdge = 200;

	/// <summary>The page itself in each look, small, for the filter cards (a few ms: a 200 px copy filtered three ways).
	/// Null clears them (page not ready).</summary>
	private async Task MakeFilterThumbsAsync(string? key, RgbImage? image)
	{
		if (image == null)
		{
			FilterThumbsChanged?.Invoke(null);
			return;
		}
		bool clean = CleanBackground;
		int darkness = (int)Math.Round(Darkness);
		FilterThumbs thumbs = await Task.Run(() =>
		{
			(int w, int h) = ImageGeometry.FitLongEdge(image.Width, image.Height, FilterThumbEdge);
			RgbImage small = image.Resize(w, h);
			int dpi = CropRenderService.PageDpi(w, h);
			return new FilterThumbs(small,
				DocumentFilter.Apply(small, new FilterOptions(PageColorMode.Gray, darkness, clean), dpi).Gray!,
				DocumentFilter.Apply(small, new FilterOptions(PageColorMode.BlackWhite, darkness, clean), dpi).Gray!);
		});
		if (key == _baseKey) FilterThumbsChanged?.Invoke(thumbs);
	}

	/// <summary>New pictures for the three filter cards (null = none). Raised on the UI thread.</summary>
	public event Action<FilterThumbs?>? FilterThumbsChanged;

	private async Task LoadBaseAsync(string pageId, string key)
	{
		_baseKey = key;
		_base = null;
		LookPreview? cached = _cache.FirstOrDefault(c => c.Key == key).Preview;
		if (cached != null)
		{
			Remember(key, cached); // most recently shown first: the one kept longest
			ShowBase(key, cached);
			_ = PrefetchNeighboursAsync();
			return;
		}

		PreviewChanged?.Invoke(null); // never leave the previous page on screen
		IsBusy = true;
		RgbImage? image = null;
		try
		{
			using (Perf.Measure("preview open (decode + warp)"))
				image = await renderer.RenderPreviewAsync(_docId!, pageId);
		}
		catch (Exception ex)
		{
			if (key == _baseKey) Status = "Không mở được ảnh: " + ex.Message;
		}
		if (key != _baseKey) return; // the user moved on meanwhile
		IsBusy = false;
		if (image == null) return;
		var preview = new LookPreview(image);
		Remember(key, preview);
		ShowBase(key, preview);
		_ = PrefetchNeighboursAsync();
	}

	private void Remember(string key, LookPreview image)
	{
		_cache.RemoveAll(c => c.Key == key);
		_cache.Insert(0, (key, image));
		if (_cache.Count > CachedPreviews) _cache.RemoveAt(_cache.Count - 1);
	}

	/// <summary>Straightens the previous and the next page in the background, so "‹ Trước / Sau ›" shows them at once.</summary>
	private async Task PrefetchNeighboursAsync()
	{
		if (_prefetching || _docId == null || _pageId == null) return;
		_prefetching = true;
		try
		{
			string docId = _docId, current = _pageId;
			foreach (int delta in new[] { 1, -1 })
			{
				var neighbour = store.Neighbor(docId, current, delta);
				PageRecord? p = neighbour == null ? null : store.Pages(docId).FirstOrDefault(x => x.Id == neighbour.Value.PageId);
				if (p is not { State: PageState.Ready }) continue;
				string key = BaseKey(p);
				if (_cache.Any(c => c.Key == key)) continue;
				RgbImage? image = await renderer.RenderPreviewAsync(docId, p.Id);
				if (image != null)
				{
					// After the shown page (kept first), before older ones.
					_cache.RemoveAll(c => c.Key == key);
					_cache.Insert(Math.Min(1, _cache.Count), (key, new LookPreview(image)));
					if (_cache.Count > CachedPreviews) _cache.RemoveAt(_cache.Count - 1);
				}
				if (_pageId != current) return;
			}
		}
		catch (Exception)
		{
			// Only a head start: the page is straightened when it is shown.
		}
		finally
		{
			_prefetching = false;
		}
	}

	/// <summary>Re-filters the preview for the current look. A request arriving while one runs replaces it (latest wins),
	/// so dragging the darkness slider never queues up stale pictures.</summary>
	private void RequestLook()
	{
		if (_base == null) return;
		if (_computing)
		{
			_dirty = true;
			return;
		}
		_ = RunLookAsync();
	}

	private async Task RunLookAsync()
	{
		_computing = true;
		try
		{
			do
			{
				_dirty = false;
				LookPreview? source = _base;
				string? key = _baseKey;
				if (source == null) return;
				// Color / gray: brightness and contrast are not baked in, the view applies them live (ToneChanged).
				// Black and white: the brightness moves the threshold, so it is part of the picture.
				// While a slider is being dragged, specks are left in (the speck pass costs twice the threshold itself);
				// the release shows the finished picture.
				var look = new FilterOptions(Mode, (int)Math.Round(Darkness), CleanBackground, Despeckle: !_dragging) { Tone = CurrentTone };
				PreviewFrame frame = look.Mode == PageColorMode.Color
					? source.Render(look)
					: await Task.Run(() =>
					{
						using (Perf.Measure($"preview filter {look.Mode}"))
							return source.Render(look);
					});
				if (_dirty || key != _baseKey) continue;
				_plainFrame = frame;
				List<PageStamp>? stamps = CurrentPage()?.Stamps;
				if (stamps is { Count: > 0 }) frame = await Task.Run(() => Stamped(frame, stamps));
				if (_dirty || key != _baseKey) continue;
				PreviewChanged?.Invoke(frame);
				PublishTone();
			}
			while (_dirty && _base != null);
		}
		finally
		{
			_computing = false;
		}
	}

	private static string StampsKey(PageRecord p) => p.Stamps is { Count: > 0 } ? string.Join(";", p.Stamps) : "";

	/// <summary>A copy of the preview with the page's signatures drawn on it (the preview stages are shared: never drawn
	/// into in place).</summary>
	private PreviewFrame Stamped(PreviewFrame frame, IReadOnlyList<PageStamp> stamps)
	{
		if (frame.Color != null)
		{
			var copy = new RgbImage(frame.Color.Width, frame.Color.Height, (byte[])frame.Color.Data.Clone());
			Stamper.Apply(copy, stamps, signatures.Ink);
			return new PreviewFrame(copy, null);
		}
		var gray = new GrayImage(frame.Gray!.Width, frame.Gray.Height, (byte[])frame.Gray.Data.Clone());
		Stamper.Apply(gray, stamps, signatures.Ink, bilevel: false);
		return new PreviewFrame(null, gray);
	}

	/// <summary>"Chữ ký": place / move / remove signatures on this page, on the picture as it looks now.</summary>
	[RelayCommand]
	private async Task SignAsync()
	{
		if (_docId == null || _pageId == null || !CanEdit || _plainFrame == null) return;
		signing.Begin(_docId, _pageId, _plainFrame);
		await Shell.Current.GoToAsync(AppShell.Routes.Signature);
	}

	private void PublishTone() => ToneChanged?.Invoke(Mode == PageColorMode.BlackWhite ? ToneAdjust.None : CurrentTone);

	/// <summary>The picture is being turned by this many degrees (+90 / -90): the view animates it on the GPU while the
	/// turned preview is prepared, then shows the turned bitmap. Raised on the UI thread.</summary>
	public event Action<int>? RotationStarted;

	/// <summary>Computes, in the background and on a share of the cores, the stages the other looks need, so that
	/// switching look or opening the adjust panel is immediate.</summary>
	private static Task WarmAsync(LookPreview preview) => Task.Run(() =>
	{
		using (ParallelScope.Limit(Math.Max(1, Environment.ProcessorCount / 2)))
		using (Perf.Measure("preview warm"))
		{
			preview.Warm(cleanBackground: true);
			preview.Warm(cleanBackground: false);
		}
	});

	#endregion

	#region Look

	/// <summary>Color / gray / black and white. The parameter is the enum name (from the XAML buttons).</summary>
	[RelayCommand]
	private void SetMode(string name)
	{
		if (!CanEdit || !Enum.TryParse(name, out PageColorMode m) || m == Mode) return;
		Mode = m;
		RequestLook();
		Save(mode: m);
	}

	/// <summary>Live while the slider moves (the preview is re-filtered); saved when it is released.</summary>
	partial void OnDarknessChanged(double value)
	{
		if (!_loading) RequestLook();
	}

	[RelayCommand]
	private void CommitDarkness()
	{
		EndDrag();
		Save(darkness: (int)Math.Round(Darkness));
	}

	/// <summary>A slider is held: black and white is previewed without the speck pass (see RunLookAsync).</summary>
	private bool _dragging;

	[RelayCommand]
	private void StartDrag() => _dragging = true;

	private void EndDrag()
	{
		if (!_dragging) return;
		_dragging = false;
		if (Mode == PageColorMode.BlackWhite) RequestLook(); // the finished picture, specks removed
	}

	partial void OnCleanBackgroundChanged(bool value)
	{
		if (_loading) return;
		RequestLook();
		Save(clean: value);
	}

	/// <summary>Live: only the color matrix of the shown picture changes, nothing is re-filtered.</summary>
	partial void OnBrightnessChanged(double value)
	{
		if (_loading) return;
		if (Mode == PageColorMode.BlackWhite) RequestLook(); // moves the threshold: one pass over the cached statistics
		else PublishTone();
	}

	partial void OnContrastChanged(double value)
	{
		if (!_loading) PublishTone();
	}

	[RelayCommand]
	private void CommitTone()
	{
		EndDrag();
		Save(brightness: CurrentTone.Brightness, contrast: CurrentTone.Contrast);
	}

	[RelayCommand]
	private void ResetTone()
	{
		if (!CanEdit) return;
		_loading = true;
		Brightness = 0;
		Contrast = 0;
		_loading = false;
		PublishTone();
		if (Mode == PageColorMode.BlackWhite) RequestLook();
		Save(brightness: 0, contrast: 0);
	}

	/// <summary>Records the change right away (a few bytes) and saves the full-size page a moment after the last change.</summary>
	private void Save(PageColorMode? mode = null, int? darkness = null, bool? clean = null, int? brightness = null, int? contrast = null)
	{
		if (_loading || _docId == null || _pageId == null || !CanEdit) return;
		if (!edit.SetFilter(_docId, _pageId, mode, darkness, clean, brightness, contrast)) return;
		SaveLater();
	}

	/// <summary>Turns the page a quarter turn (+90 clockwise / -90 counter-clockwise). The preview in memory is turned at
	/// once (a few ms), not straightened again; the full-size page is saved turned a moment later.</summary>
	[RelayCommand]
	private void Rotate(int degrees)
	{
		if (_docId == null || _pageId == null || !CanEdit) return;
		if (!edit.RotateOutput(_docId, _pageId, degrees)) return;
		PageRecord? page = CurrentPage();
		if (page == null) return;
		string key = BaseKey(page);
		if (_base != null)
		{
			RotationStarted?.Invoke(degrees); // the view turns the picture on the GPU right away
			LookPreview turned;
			using (Perf.Measure("preview turn"))
				turned = _base.RotateClockwise(degrees / 90); // every stage turned, nothing filtered again
			Remember(key, turned);
			ShowBase(key, turned);
		}
		else if (key != _baseKey)
		{
			_ = LoadBaseAsync(page.Id, key); // the preview was still loading: load it turned
		}
		SaveLater();
	}

	private void SaveLater()
	{
		_requested = false;
		if (CurrentPage() is { } page) UpdateStatus(page);

		_saveDelay?.Cancel();
		var delay = _saveDelay = new CancellationTokenSource();
		_ = SaveLaterAsync(delay);
	}

	private async Task SaveLaterAsync(CancellationTokenSource delay)
	{
		try
		{
			await Task.Delay(SaveDelay, delay.Token);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		if (_saveDelay != delay) return;
		SaveNow();
	}

	private void SaveNow()
	{
		_saveDelay = null;
		if (CurrentPage() is { } page) EnsureSaved(page);
	}

	/// <summary>Gives every page of the document this page's look; the other pages are saved again in the background.</summary>
	[RelayCommand]
	private async Task ApplyToAllAsync()
	{
		if (_docId == null || !CanEdit) return;
		var look = new FilterOptions(Mode, (int)Math.Round(Darkness), CleanBackground) { Tone = CurrentTone };
		IReadOnlyList<string> changed = edit.ApplyFilterToAll(_docId, look);
		foreach (string id in changed)
			if (id != _pageId) queue.EnqueueRender(_docId, id);
		_requested = false;
		SaveNow();
		string text = changed.Count == 0 ? "Mọi trang đã dùng kiểu này." : $"Đã áp dụng cho {changed.Count} trang; các trang đang được lưu lại.";
		await Shell.Current.DisplayAlertAsync("Áp dụng cho mọi trang", text, "OK");
	}

	#endregion

	/// <summary>A4 (standard sheet) or the outline's own proportions (receipts, cards, other paper).</summary>
	[RelayCommand]
	private void ToggleAspect()
	{
		if (_docId == null || _pageId == null || !CanEdit) return;
		PageRecord? page = CurrentPage();
		if (page == null || page.State != PageState.Ready) return;
		edit.SetFreeAspect(_docId, _pageId, !page.FreeAspect);
		_requested = false; // the page is stale now; the new shape also means a new preview
		Refresh();
	}

	[RelayCommand]
	private void Retry()
	{
		if (_docId == null || _pageId == null) return;
		store.Update(_docId, d =>
		{
			PageRecord? p = d.Pages.FirstOrDefault(x => x.Id == _pageId);
			if (p != null) p.RenderError = null;
		});
		_requested = false;
		Refresh();
	}

	/// <summary>Back to the outline editor, on the page shown here (which may differ from the one opened).</summary>
	[RelayCommand]
	private Task EditAgainAsync() => Shell.Current.GoToAsync($"..?docId={_docId}&pageId={_pageId}");

	/// <summary>Previous (-1) / next (+1) page: buttons or a horizontal swipe on the picture.</summary>
	[RelayCommand]
	private void Go(int delta)
	{
		if (_docId == null || _pageId == null) return;
		var next = store.Neighbor(_docId, _pageId, delta);
		if (next == null) return;
		SaveNow(); // the page being left is saved without waiting
		_pageId = next.Value.PageId;
		_requested = false;
		Refresh();
	}

	[RelayCommand]
	private void SwipeLeft() => Go(1);

	[RelayCommand]
	private void SwipeRight() => Go(-1);

	[RelayCommand]
	private Task ExportPdfAsync() => _docId == null ? Task.CompletedTask : exports.ExportAsync(_docId);
}
