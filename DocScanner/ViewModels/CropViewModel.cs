using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocScanner.Core;
using DocScanner.Services;

namespace DocScanner.ViewModels;

/// <summary>
/// One page with its paper outline, which the user can adjust (drag corners / sides, "Tự động",
/// "Toàn ảnh", rotate). The page may still be in the background pipeline when it is opened: this
/// screen shows what exists and updates itself as the pipeline advances.
/// </summary>
public partial class CropViewModel(DocumentStore store, CropDetectionService detection, PageEditService edit, PageIngestQueue queue,
	ExportCoordinator exports)
	: ObservableObject, IQueryAttributable
{
	private string? _docId;
	private string? _pageId;
	private bool _detecting;

	[ObservableProperty]
	private string title = "Cắt tài liệu";

	/// <summary>File shown by the editor; null while there is nothing (or nothing current) to show.</summary>
	[ObservableProperty]
	private string? imagePath;

	[ObservableProperty]
	private double[]? quad;

	/// <summary>Curved sides of the detected outline (PageRecord.CropBend); null once the outline was moved by hand.</summary>
	[ObservableProperty]
	private double[]? bend;

	[ObservableProperty]
	private bool detected = true;

	[ObservableProperty]
	private bool manual;

	[ObservableProperty]
	private string status = "";

	/// <summary>Buttons work only on a finished page (a rotation while it is being rebuilt would race the rebuild).</summary>
	[ObservableProperty]
	private bool canEdit;

	[ObservableProperty]
	private bool canGoPrevious;

	[ObservableProperty]
	private bool canGoNext;

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		if (query.TryGetValue("docId", out object? d) && d is string docId
			&& query.TryGetValue("pageId", out object? p) && p is string pageId)
		{
			_docId = docId;
			_pageId = pageId;
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
		PrerenderLeftPage();
	}

	/// <summary>The user is done with this page's outline: straighten it in the background, so its result
	/// and the export are ready sooner (a render the result screen asks for overtakes it).</summary>
	private void PrerenderLeftPage()
	{
		if (_docId != null && _pageId != null) queue.EnqueuePrerender(_docId, _pageId);
	}

	private void OnPageUpdated(PageUpdate update)
	{
		if (update.PageId == _pageId) MainThread.BeginInvokeOnMainThread(Refresh);
	}

	private void Refresh()
	{
		if (_docId == null || _pageId == null) return;
		IReadOnlyList<PageRecord> pages = store.Pages(_docId);
		PageRecord? page = pages.FirstOrDefault(x => x.Id == _pageId);
		if (page == null)
		{
			Status = "Không tìm thấy trang.";
			CanEdit = false;
			return;
		}
		int index = pages.ToList().IndexOf(page);
		Title = $"Trang {index + 1}/{pages.Count}";
		CanGoPrevious = index > 0;
		CanGoNext = index < pages.Count - 1;

		switch (page.State)
		{
			case PageState.Failed:
				CanEdit = false;
				Status = "Không đọc được ảnh: " + page.Error;
				return;

			case PageState.Importing:
				// A placeholder: the photo is not in the document yet. Everything stays locked until it is.
				ImagePath = null;
				Quad = null;
				CanEdit = false;
				Status = "Ảnh đang được tải vào, chờ một chút...";
				return;

			case PageState.Pending:
				// Being (re)built: show nothing rather than a picture that is the wrong way up.
				ImagePath = null;
				Quad = null;
				CanEdit = false;
				Status = "Đang xử lý ảnh...";
				return;

			case PageState.Preview:
				// The thumbnail is there, the proxy is not yet: a quick look, no editing.
				ImagePath = store.ThumbPath(_docId, page);
				Quad = null;
				CanEdit = false;
				Status = "Đang xử lý ảnh...";
				return;
		}

		CanEdit = true;
		ImagePath = store.ProxyPath(_docId, page);

		if (page.CropQuad == null)
		{
			// No outline yet: start from the whole frame so the handles can already be used.
			Bend = null;
			Quad = ImageCoreService.Quad.Inset(0.03).ToValues();
			Detected = false;
			Manual = false;
			Status = "Đang dò mép giấy...";
			// The pipeline normally does this in the background; only step in when it is not going to.
			if (!queue.IsBusy(page.Id) && !_detecting) _ = DetectNowAsync(page.Id, overrideManual: false);
			return;
		}

		Bend = page.CropManual ? null : page.CropBend;
		Quad = page.CropQuad;
		Detected = page.CropDetected;
		Manual = page.CropManual;
		Status = page.CropManual
			? "Đã chỉnh tay"
			: page.CropDetected
				? $"Tự dò mép giấy · tin cậy {page.CropConfidence:P0}"
				: "Không dò được · kéo 4 góc vào tờ giấy";
	}

	private async Task DetectNowAsync(string pageId, bool overrideManual)
	{
		if (_docId == null) return;
		_detecting = true;
		try
		{
			await detection.DetectAsync(_docId, pageId, default, overrideManual);
		}
		catch (Exception ex)
		{
			Status = "Không dò được mép giấy: " + ex.Message;
			_detecting = false;
			return;
		}
		_detecting = false;
		Refresh();
	}

	/// <summary>Called by the editor when a drag ends.</summary>
	[RelayCommand]
	private void Edited(double[] values)
	{
		if (_docId == null || _pageId == null || values is not { Length: 8 }) return;
		edit.SetCrop(_docId, _pageId, ImageCoreService.Quad.FromValues(values));
		Bend = null;
		Manual = true;
		Status = "Đã chỉnh tay";
	}

	[RelayCommand]
	private async Task AutoAsync()
	{
		if (_docId == null || _pageId == null || !CanEdit) return;
		Status = "Đang dò mép giấy...";
		await DetectNowAsync(_pageId, overrideManual: true);
	}

	[RelayCommand]
	private void FullImage()
	{
		if (_docId == null || _pageId == null || !CanEdit) return;
		edit.UseFullImage(_docId, _pageId);
		Refresh();
	}

	[RelayCommand]
	private void Rotate()
	{
		if (_docId == null || _pageId == null || !CanEdit) return;
		if (edit.Rotate(_docId, _pageId)) Refresh(); // Pending now; the pipeline reports back when the page is rebuilt
	}

	/// <summary>Straighten the page and show the result (color / gray / black and white).</summary>
	[RelayCommand]
	private async Task ShowResultAsync()
	{
		if (_docId == null || _pageId == null || !CanEdit) return;
		KeepShownOutline();
		await Shell.Current.GoToAsync($"{AppShell.Routes.Result}?docId={_docId}&pageId={_pageId}");
	}

	/// <summary>Previous (-1) / next (+1) page of the document, from the buttons or a swipe outside the outline.
	/// The outline on screen is kept for the page being left.</summary>
	[RelayCommand]
	private void Go(int delta)
	{
		if (_docId == null || _pageId == null) return;
		var next = store.Neighbor(_docId, _pageId, delta);
		if (next == null) return;
		KeepShownOutline();
		PrerenderLeftPage();
		_pageId = next.Value.PageId;
		_detecting = false;
		Refresh();
	}

	[RelayCommand]
	private Task ExportPdfAsync()
	{
		if (_docId == null) return Task.CompletedTask;
		KeepShownOutline();
		return exports.ExportAsync(_docId);
	}

	/// <summary>An untouched page still shows the fallback frame (never saved): that is what the user accepted.</summary>
	private void KeepShownOutline()
	{
		if (_docId == null || _pageId == null || !CanEdit) return;
		PageRecord? page = store.Pages(_docId).FirstOrDefault(p => p.Id == _pageId);
		if (page is { State: PageState.Ready, CropQuad: null } && Quad is { Length: 8 })
			edit.SetCrop(_docId, _pageId, ImageCoreService.Quad.FromValues(Quad));
	}
}
