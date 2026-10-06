using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocScanner.Core;
using DocScanner.Services;

namespace DocScanner.ViewModels;

/// <summary>One document: its pages (filling in as the background pipeline works), adding more photos,
/// reordering / deleting pages with one level of undo, renaming, and exporting a PDF.</summary>
public partial class DocumentViewModel(DocumentStore store, ImportCoordinator importer, BackgroundImporter imports,
	PageIngestQueue queue, ExportCoordinator exports)
	: ObservableObject, IQueryAttributable
{
	private string? _docId;
	private PageItem? _dragged;

	/// <summary>The last undoable change (page moved or deleted); null when there is nothing to undo.</summary>
	private Func<bool>? _undo;

	public ObservableCollection<PageItem> Pages { get; } = [];

	[ObservableProperty]
	private string title = "";

	/// <summary>"Đang nhập ảnh 23/100 · đang xử lý 12" while photos are copied / pages prepared in the background, or
	/// what is left to report once the import ended (failures, stopped).</summary>
	[ObservableProperty]
	private string progressText = "";

	[ObservableProperty]
	private bool hasProgress;

	/// <summary>The button next to the status: "Dừng" while importing, "Đóng" / "Xem" for a finished import's report.</summary>
	[ObservableProperty]
	private string importActionText = "";

	[ObservableProperty]
	private bool hasImportAction;

	/// <summary>Photos are being copied in right now (spinner on the status line).</summary>
	[ObservableProperty]
	private bool isImporting;

	/// <summary>Text of the undo bar ("Đã xoá Trang 3"); the bar shows while it is not empty.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanUndo))]
	private string undoText = "";

	public bool CanUndo => UndoText.Length > 0;

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		if (query.TryGetValue("docId", out object? id) && id is string s)
		{
			if (s == _docId)
			{
				Sync();
				return;
			}
			ClearUndo();
			_docId = s;
			Reload();
		}
	}

	/// <summary>Follow the background pipeline while this screen is visible.</summary>
	public void Attach()
	{
		queue.PageUpdated += OnPageUpdated;
		imports.Changed += OnImportChanged;
		imports.PageChanged += OnImportPageChanged;
		Sync(); // back from another screen: update the tiles in place instead of rebuilding them all
	}

	public void Detach()
	{
		queue.PageUpdated -= OnPageUpdated;
		imports.Changed -= OnImportChanged;
		imports.PageChanged -= OnImportPageChanged;
	}

	/// <summary>Tiles by page id: the background work reports one page at a time, and with 100 tiles a linear search per
	/// report adds up.</summary>
	private readonly Dictionary<string, PageItem> _items = [];

	private void OnPageUpdated(PageUpdate update) => RefreshPageLater(update.DocId, update.PageId);

	/// <summary>A placeholder's photo is being copied, or it was filled / failed.</summary>
	private void OnImportPageChanged(string docId, string pageId) => RefreshPageLater(docId, pageId);

	/// <summary>Updates only that tile, in place: the list itself is not touched, so it does not jump or flicker.</summary>
	private void RefreshPageLater(string docId, string pageId) =>
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (docId != _docId) return;
			if (_items.TryGetValue(pageId, out PageItem? item)) item.Refresh();
			else Sync();
			UpdateProgress();
		});

	/// <summary>Placeholders were added (a pick) or taken away (a stop), or the import's status changed.</summary>
	private void OnImportChanged(string docId) =>
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (docId != _docId) return;
			Sync();
			UpdateProgress();
		});

	/// <summary>Brings the tiles in line with the document with as little change to the list as possible: the same pages
	/// in the same order = refresh each tile in place; pages only added at the end (a pick adds all its placeholders at
	/// once) = append; anything else (reorder, removal) = rebuild.</summary>
	private void Sync()
	{
		if (_docId == null) return;
		DocumentRecord? doc = store.Get(_docId);
		if (doc == null) return;
		Title = doc.Name;
		IReadOnlyList<PageRecord> pages = store.Pages(_docId);
		bool prefix = Pages.Count <= pages.Count;
		for (int i = 0; prefix && i < Pages.Count; i++)
			prefix = ReferenceEquals(Pages[i].Record, pages[i]);
		if (!prefix)
		{
			Reload();
			return;
		}
		// Only the tiles still mid-pipeline: a settled (Ready/Failed) tile only ever changes through OnPageUpdated /
		// OnImportPageChanged, which already target it directly. Refreshing every tile here too made a big import
		// O(n) PER TICK (this fires on roughly every photo of the batch) -- the file-exists checks alone turned a
		// 100-photo import into tens of thousands of them, the opposite of the "it should feel instant" this exists for.
		foreach (PageItem item in Pages)
			if (item.Record.State is PageState.Importing or PageState.Pending or PageState.Preview)
				item.Refresh();
		for (int i = Pages.Count; i < pages.Count; i++) Add(NewItem(pages[i], i + 1));
		UpdateProgress();
	}

	private void Add(PageItem item)
	{
		Pages.Add(item);
		_items[item.Record.Id] = item;
	}

	private PageItem NewItem(PageRecord p, int number) =>
		new(p, number, ThumbFor, OpenPage, DeletePage, ShowPageMenu, x => _dragged = x, DropOn,
			imports.CopyProgress, queue.IsPreparing);

	/// <summary>Rebuilds the tiles from the document (after a reorder or a deletion).</summary>
	public void Reload()
	{
		if (_docId == null) return;
		DocumentRecord? doc = store.Get(_docId);
		if (doc == null) return;

		Title = doc.Name;
		IReadOnlyList<PageRecord> pages = store.Pages(_docId);
		Pages.Clear();
		_items.Clear();
		for (int i = 0; i < pages.Count; i++)
			Add(NewItem(pages[i], i + 1));
		UpdateProgress();
	}
	/// <summary>The straightened thumbnail once there is a current one, else the plain thumbnail.</summary>
	private string ThumbFor(PageRecord p) =>
		p.CroppedRevision > 0 && !p.NeedsRender
			? store.CroppedThumbPath(_docId!, p.Id, p.CroppedRevision)
			: store.ThumbPath(_docId!, p);

	private void UpdateProgress()
	{
		int busy = Pages.Count(p => p.Record.State is PageState.Pending or PageState.Preview);
		string preparing = busy > 0 ? $"đang xử lý {busy} ảnh" : "";
		ImportStatus? import = _docId == null ? null : imports.Status(_docId);

		if (import is { Running: true })
		{
			ProgressText = $"Đang nhập ảnh {Math.Min(import.Done + 1, import.Total)}/{import.Total}"
				+ (import.Stopped ? " · đang dừng..." : "") + (busy > 0 ? " · " + preparing : "");
			ImportActionText = "Dừng";
			HasImportAction = !import.Stopped;
		}
		else if (import != null)
		{
			int copied = import.Done - import.Failures.Count;
			string report = import.Stopped
				? $"Đã dừng: nhập {copied}/{import.Total} ảnh"
				: $"Không nhập được {import.Failures.Count}/{import.Total} ảnh";
			ProgressText = report + (busy > 0 ? " · " + preparing : "");
			ImportActionText = import.Failures.Count > 0 ? "Xem" : "Đóng";
			HasImportAction = true;
		}
		else
		{
			ProgressText = busy > 0 ? $"Đang xử lý {busy} ảnh..." : "";
			HasImportAction = false;
		}
		IsImporting = import is { Running: true } || busy > 0;
		HasProgress = ProgressText.Length > 0;
	}

	/// <summary>"Dừng" stops the import (pages already added stay); "Xem" / "Đóng" shows what failed and clears the report.</summary>
	[RelayCommand]
	private async Task ImportActionAsync()
	{
		if (_docId == null) return;
		string docId = _docId;
		ImportStatus? import = imports.Status(docId);
		if (import == null) return;
		if (import.Running)
		{
			imports.Stop(docId);
			return;
		}
		if (import.Failures.Count > 0)
		{
			string names = string.Join("\n", import.Failures.Take(8).Select(f => "• " + (f.Name.Length > 0 ? f.Name : f.Message)));
			string more = import.Failures.Count > 8 ? $"\n... và {import.Failures.Count - 8} ảnh khác" : "";
			await Shell.Current.DisplayAlertAsync("Ảnh không nhập được", $"{import.Failures.Count} ảnh không đọc được:\n{names}{more}", "OK");
		}
		imports.Dismiss(docId);
	}

	[RelayCommand]
	private Task AddFromGalleryAsync() => AddAsync(importer.FromGalleryAsync);

	[RelayCommand]
	private Task AddFromCameraAsync() => AddAsync(importer.FromCameraAsync);

	[RelayCommand]
	private Task AddFromPdfAsync() => AddAsync(importer.FromPdfAsync);

	/// <summary>Picks and returns: the photos are copied in the background and appear one by one.</summary>
	private async Task AddAsync(Func<Func<DocumentRecord>, Task<DocumentRecord?>> pick)
	{
		if (_docId == null) return;
		DocumentRecord? doc = store.Get(_docId);
		if (doc == null) return;
		await pick(() => doc);
		UpdateProgress();
	}

	private const string ModePreference = "document_open_mode";

	/// <summary>What tapping a page does: "Xem" opens the page viewer (zoom, swipe through the pages), "Sửa" the editor.
	/// Remembered between documents and app starts.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsEditMode), nameof(ModeHint))]
	private bool isViewMode = Preferences.Default.Get(ModePreference, "view") == "view";

	public bool IsEditMode => !IsViewMode;

	public string ModeHint => IsViewMode ? "Chạm trang để xem, phóng to" : "Chạm trang để sửa khung, bộ lọc";

	partial void OnIsViewModeChanged(bool value) => Preferences.Default.Set(ModePreference, value ? "view" : "edit");

	[RelayCommand]
	private void SetMode(string mode) => IsViewMode = mode == "view";

	private void OpenPage(PageItem item)
	{
		if (_docId == null) return;
		if (item.Record.State == PageState.Failed)
		{
			_ = Shell.Current.DisplayAlertAsync("Không đọc được ảnh", item.Record.Error ?? "Định dạng không hỗ trợ.", "OK");
			return;
		}
		string route = IsViewMode ? AppShell.Routes.Viewer : AppShell.Routes.Crop;
		_ = Shell.Current.GoToAsync($"{route}?docId={_docId}&pageId={item.Record.Id}");
	}

	/// <summary>"Chỉnh sửa" on the bottom bar: the editor on the first page that can be edited (then ‹ › through the rest).</summary>
	[RelayCommand]
	private void EditPages()
	{
		PageItem? first = Pages.FirstOrDefault(p => p.Record.State == PageState.Ready)
			?? Pages.FirstOrDefault(p => p.Record.State != PageState.Failed);
		if (first != null) OpenPage(first);
	}

	#region Reorder / delete / undo

	/// <summary>Deletes at once (no confirmation): the undo bar can bring the page back.</summary>
	private void DeletePage(PageItem item)
	{
		if (_docId == null) return;
		string docId = _docId;
		store.EmptyTrash(docId); // only the most recent deletion is undoable
		DeletedPage? deleted = store.TrashPage(docId, item.Record.Id);
		if (deleted == null) return;
		SetUndo($"Đã xoá {item.Label}", () => store.RestorePage(deleted));
		// Only that tile leaves the list (the rest are renumbered in place): no rebuild, no flicker.
		Pages.Remove(item);
		_items.Remove(item.Record.Id);
		Renumber();
		UpdateProgress();
	}

	private void MovePage(PageItem item, int newIndex)
	{
		if (_docId == null) return;
		List<string> before = Pages.Select(p => p.Record.Id).ToList();
		if (!store.MovePage(_docId, item.Record.Id, newIndex)) return;
		string docId = _docId;
		SetUndo($"Đã chuyển {item.Label}", () => store.SetOrder(docId, before));
		int oldIndex = Pages.IndexOf(item);
		int target = Math.Clamp(newIndex, 0, Pages.Count - 1);
		if (oldIndex >= 0 && oldIndex != target) Pages.Move(oldIndex, target);
		Renumber();
		Sync(); // falls back to a rebuild if the store's order differs from the list's
	}

	private void Renumber()
	{
		for (int i = 0; i < Pages.Count; i++) Pages[i].SetNumber(i + 1);
	}

	/// <summary>Drag and drop: the dragged tile takes the place of the tile it is dropped on.</summary>
	private void DropOn(PageItem target)
	{
		PageItem? dragged = _dragged;
		_dragged = null;
		if (dragged == null || dragged == target) return;
		int to = Pages.IndexOf(target);
		if (to >= 0) MovePage(dragged, to);
	}

	private async void ShowPageMenu(PageItem item)
	{
		int index = Pages.IndexOf(item), last = Pages.Count - 1;
		var actions = new List<string> { "Mở / chỉnh khung" };
		if (index > 0) actions.AddRange(["Đưa lên đầu", "Lên trước 1 trang"]);
		if (index < last) actions.AddRange(["Ra sau 1 trang", "Đưa xuống cuối"]);
		string? choice = await Shell.Current.DisplayActionSheetAsync(item.Label, "Đóng", "Xoá trang", [.. actions]);
		switch (choice)
		{
			case "Mở / chỉnh khung": OpenPage(item); break;
			case "Đưa lên đầu": MovePage(item, 0); break;
			case "Lên trước 1 trang": MovePage(item, index - 1); break;
			case "Ra sau 1 trang": MovePage(item, index + 1); break;
			case "Đưa xuống cuối": MovePage(item, last); break;
			case "Xoá trang": DeletePage(item); break;
		}
	}

	private void SetUndo(string text, Func<bool> undo)
	{
		_undo = undo;
		UndoText = text;
	}

	private void ClearUndo()
	{
		_undo = null;
		UndoText = "";
		if (_docId != null) store.EmptyTrash(_docId);
	}

	[RelayCommand]
	private void Undo()
	{
		Func<bool>? undo = _undo;
		_undo = null;
		UndoText = "";
		if (undo != null && !undo())
			_ = Shell.Current.DisplayAlertAsync("Không hoàn tác được", "Trang đã bị xoá hẳn.", "OK");
		Reload();
	}

	[RelayCommand]
	private void DismissUndo() => ClearUndo();

	#endregion

	[RelayCommand]
	private async Task RenameAsync()
	{
		if (_docId == null) return;
		string? name = await Shell.Current.DisplayPromptAsync("Đổi tên tài liệu", "Tên mới:", "Lưu", "Huỷ",
			initialValue: Title, maxLength: 120, keyboard: Keyboard.Text);
		if (name != null && store.Rename(_docId, name)) Title = store.Get(_docId)!.Name;
	}

	[RelayCommand]
	private Task ExportPdfAsync()
	{
		if (_docId == null) return Task.CompletedTask;
		ClearUndo();
		return exports.ExportAsync(_docId);
	}
}
