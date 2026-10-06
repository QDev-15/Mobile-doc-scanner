using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocScanner.Core;
using DocScanner.Services;

namespace DocScanner.ViewModels;

/// <summary>
/// Look at the pages of a document (the "Xem" mode of the document screen): the finished page at full resolution,
/// zoomable, one after another. Shows the best picture there is: the straightened page (while a newer one is being made,
/// the previous one, then the new one as soon as it is saved), else the photo.
/// </summary>
public partial class ViewerViewModel(DocumentStore store, PageIngestQueue queue, ExportCoordinator exports)
	: ObservableObject, IQueryAttributable
{
	private string? _docId, _pageId, _shownPath;

	[ObservableProperty]
	private string title = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasStatus))]
	private string status = "";

	public bool HasStatus => Status.Length > 0;

	[ObservableProperty]
	private bool canGoPrevious;

	[ObservableProperty]
	private bool canGoNext;

	/// <summary>A new picture to show (a file path), or null for none.</summary>
	public event Action<string?>? PictureChanged;

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

	public void Detach() => queue.PageUpdated -= OnPageUpdated;

	private void OnPageUpdated(PageUpdate update)
	{
		if (update.DocId == _docId && update.PageId == _pageId) MainThread.BeginInvokeOnMainThread(Refresh);
	}

	private void Refresh()
	{
		if (_docId == null || _pageId == null) return;
		IReadOnlyList<PageRecord> pages = store.Pages(_docId);
		int index = pages.ToList().FindIndex(x => x.Id == _pageId);
		if (index < 0)
		{
			Title = "";
			Show(null);
			return;
		}
		PageRecord page = pages[index];
		Title = $"Trang {index + 1}/{pages.Count}";
		CanGoPrevious = index > 0;
		CanGoNext = index < pages.Count - 1;

		(string? path, bool final) = BestPicture(page);
		if (!final && page.State == PageState.Ready && !queue.IsPreparing(page.Id)) queue.EnqueueRender(_docId, page.Id);
		Status = page.State switch
		{
			PageState.Failed => "Không đọc được ảnh",
			_ when !final => "Đang chuẩn bị trang...",
			_ => "",
		};
		Show(path);
	}

	/// <summary>The straightened page if there is one (final when it is up to date), else the photo.</summary>
	private (string? Path, bool Final) BestPicture(PageRecord page)
	{
		if (page.CroppedRevision > 0)
		{
			string cropped = store.CroppedPath(_docId!, page);
			if (File.Exists(cropped)) return (cropped, !page.NeedsRender);
		}
		if (page.State == PageState.Ready) return (store.ProxyPath(_docId!, page), false);
		string thumb = store.ThumbPath(_docId!, page);
		return (File.Exists(thumb) ? thumb : null, false);
	}

	private void Show(string? path)
	{
		if (path == _shownPath) return;
		_shownPath = path;
		PictureChanged?.Invoke(path);
	}

	[RelayCommand]
	private void Go(int delta)
	{
		if (_docId == null || _pageId == null) return;
		if (store.Neighbor(_docId, _pageId, delta) is not { } next) return;
		_pageId = next.PageId;
		Refresh();
	}

	/// <summary>"Sửa": this page in the editor (outline first, as from the document in edit mode).</summary>
	[RelayCommand]
	private Task EditAsync() => _docId == null || _pageId == null
		? Task.CompletedTask
		: Shell.Current.GoToAsync($"{AppShell.Routes.Crop}?docId={_docId}&pageId={_pageId}");

	[RelayCommand]
	private async Task ShareAsync()
	{
		if (_shownPath == null || !File.Exists(_shownPath)) return;
		await Share.Default.RequestAsync(new ShareFileRequest { Title = Title, File = new ShareFile(_shownPath) });
	}

	[RelayCommand]
	private Task ExportPdfAsync() => _docId == null ? Task.CompletedTask : exports.ExportAsync(_docId);
}
