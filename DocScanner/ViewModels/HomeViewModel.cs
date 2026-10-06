using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocScanner.Core;
using DocScanner.Services;

namespace DocScanner.ViewModels;

/// <summary>
/// The main screen: folders first (by name), then the documents (newest first), at the top level or inside one folder
/// (the same screen, opened on route "folder"). Search filters by name, ignoring accents, across every folder. Selection
/// mode (long press a document, or ⋮ > Chọn nhiều): tick documents, then move them into a folder or delete them; a long
/// press also drags the selection onto a folder. Moving never reorders anything: documents are listed by creation time.
/// </summary>
public partial class HomeViewModel(DocumentStore store, ImportCoordinator importer, BackgroundImporter imports, PageIngestQueue queue,
	ExportCoordinator exports)
	: ObservableObject, IQueryAttributable
{
	private static bool _resumed;
	private string? _folderId;
	private DocumentItem? _dragged;

	public ObservableCollection<HomeItem> Items { get; } = [];

	[ObservableProperty]
	private string title = "Doc Scanner";

	[ObservableProperty]
	private bool isSearching;

	[ObservableProperty]
	private string searchText = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SelectionText), nameof(NotSelecting))]
	private bool isSelecting;

	public bool NotSelecting => !IsSelecting;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SelectionText))]
	private int selectedCount;

	public string SelectionText => $"Đã chọn {SelectedCount}";

	/// <summary>Nothing to show (empty folder / no match): the placeholder text.</summary>
	[ObservableProperty]
	private string emptyText = "Bấm nút chụp bên dưới, hoặc nhập ảnh có sẵn.";

	public bool InFolder => _folderId != null;

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		if (query.TryGetValue("folderId", out object? f) && f is string id) _folderId = id;
	}

	/// <summary>Follow the background pipeline while this screen is visible.</summary>
	public void Attach()
	{
		queue.PageUpdated += OnPageUpdated;
		imports.Changed += OnImportChanged;
	}

	public void Detach()
	{
		queue.PageUpdated -= OnPageUpdated;
		imports.Changed -= OnImportChanged;
	}

	private void OnPageUpdated(PageUpdate update) => RefreshLater(update.DocId);

	private void OnImportChanged(string docId) => RefreshLater(docId);

	private void RefreshLater(string docId) =>
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (Items.OfType<DocumentItem>().FirstOrDefault(d => d.Record.Id == docId) is { } item) RefreshItem(item);
		});

	private void RefreshItem(DocumentItem item)
	{
		IReadOnlyList<PageRecord> pages = store.Pages(item.Record.Id);
		item.Refresh(pages, pages.Count > 0 ? CoverThumb(item.Record.Id, pages[0]) : null, imports.Status(item.Record.Id));
	}

	/// <summary>The first page as the user sees it: straightened once it has a current render.</summary>
	private string CoverThumb(string docId, PageRecord p) =>
		p.CroppedRevision > 0 && !p.NeedsRender ? store.CroppedThumbPath(docId, p.Id, p.CroppedRevision) : store.ThumbPath(docId, p);

	partial void OnSearchTextChanged(string value) => _ = RefreshAsync();

	[RelayCommand]
	public async Task RefreshAsync()
	{
		IReadOnlyList<DocumentRecord> docs = await Task.Run(store.List);
		IReadOnlyList<FolderRecord> folders = store.Folders();
		Perf.Log($"startup: {docs.Count} documents listed");

		FolderRecord? current = _folderId == null ? null : folders.FirstOrDefault(f => f.Id == _folderId);
		Title = current == null ? "Doc Scanner"
			: string.Join(" / ", store.Ancestors(current.Id).Reverse().Select(f => f.Name).Append(current.Name));
		string query = SearchText.Trim();
		bool searching = query.Length > 0;

		var selected = Items.OfType<DocumentItem>().Where(i => i.IsSelected).Select(i => i.Record.Id).ToHashSet();
		Items.Clear();
		// Folders first: sub-folders of the current one normally, or -- while searching -- every matching folder
		// anywhere in the tree (search is global, see this class's own doc comment), not just the current level.
		IEnumerable<FolderRecord> shownFolders = searching
			? folders.Where(f => TextSearch.Matches(f.Name, query))
			: store.ChildFolders(_folderId);
		foreach (FolderRecord f in shownFolders)
			Items.Add(new FolderItem(f, docs.Count(d => d.FolderId == f.Id), folders.Count(x => x.ParentFolderId == f.Id),
				OpenFolder, ShowFolderMenu, DropOnFolder));
		IEnumerable<DocumentRecord> shown = searching
			? docs.Where(d => TextSearch.Matches(d.Name, query))
			: docs.Where(d => d.FolderId == _folderId);
		foreach (DocumentRecord d in shown)
		{
			var item = new DocumentItem(d, OpenDocument, DeleteDocument, ShowDocumentMenu, StartDrag)
			{
				IsSelecting = IsSelecting,
				IsSelected = selected.Contains(d.Id),
			};
			RefreshItem(item);
			Items.Add(item);
		}
		SelectedCount = Items.OfType<DocumentItem>().Count(i => i.IsSelected);
		EmptyText = searching ? $"Không có tài liệu nào khớp \"{query}\"."
			: InFolder ? "Thư mục trống. Chuyển tài liệu vào bằng ⋮ > Chuyển vào thư mục, hoặc chụp ngay tại đây."
			: "Bấm nút chụp bên dưới, hoặc nhập ảnh có sẵn.";

		// Once per app run: continue whatever an earlier run left unfinished (killed mid-batch).
		if (!_resumed)
		{
			_resumed = true;
			foreach (DocumentRecord d in docs)
			{
				store.EmptyTrash(d.Id); // pages deleted in an earlier run can no longer be undone
				store.RemoveUnfinishedImports(d.Id); // photos an earlier run never got to copy
			}
			foreach (DocumentItem item in Items.OfType<DocumentItem>()) RefreshItem(item); // page counts without those placeholders
			Perf.Log("startup: rows built");
			await Task.Run(queue.ResumePending);
		}
	}

	#region Search

	[RelayCommand]
	private void ToggleSearch()
	{
		IsSearching = !IsSearching;
		if (!IsSearching) SearchText = "";
	}

	#endregion

	#region New documents

	[RelayCommand]
	private Task PickFromGalleryAsync() => ImportIntoNewDocumentAsync(importer.FromGalleryAsync);

	[RelayCommand]
	private Task CaptureAsync() => ImportIntoNewDocumentAsync(importer.FromCameraAsync);

	[RelayCommand]
	private Task PickPdfAsync() => ImportIntoNewDocumentAsync(importer.FromPdfAsync);

	/// <summary>Pick, then go straight into the new document (created in the folder being shown).</summary>
	private async Task ImportIntoNewDocumentAsync(Func<Func<DocumentRecord>, Task<DocumentRecord?>> pick)
	{
		DocumentRecord? doc = await pick(() =>
		{
			DocumentRecord created = store.Create();
			if (_folderId != null) store.MoveToFolder([created.Id], _folderId);
			return created;
		});
		if (doc != null) await OpenAsync(doc.Id);
	}

	#endregion

	#region Documents

	private void OpenDocument(DocumentItem item)
	{
		if (IsSelecting)
		{
			item.IsSelected = !item.IsSelected;
			SelectedCount = Items.OfType<DocumentItem>().Count(i => i.IsSelected);
			return;
		}
		_ = OpenAsync(item.Record.Id);
	}

	private static Task OpenAsync(string docId) =>
		Shell.Current.GoToAsync($"{AppShell.Routes.Document}?docId={docId}");

	private void DeleteDocument(DocumentItem item) => _ = ConfirmDeleteAsync([item]);

	private async Task ConfirmDeleteAsync(IReadOnlyList<DocumentItem> items)
	{
		if (items.Count == 0) return;
		string what = items.Count == 1
			? $"\"{items[0].Name}\" cùng {store.Pages(items[0].Record.Id).Count} trang"
			: $"{items.Count} tài liệu đã chọn";
		bool ok = await Shell.Current.DisplayAlertAsync("Xoá tài liệu", $"Xoá {what}? Không thể hoàn tác.", "Xoá", "Giữ lại");
		if (!ok) return;
		foreach (DocumentItem item in items)
		{
			imports.Stop(item.Record.Id); // photos still waiting would otherwise go into a deleted document
			await Task.Run(() => store.Delete(item.Record.Id));
			Items.Remove(item);
		}
		EndSelection();
	}

	private void ShowDocumentMenu(DocumentItem item) => _ = DocumentMenuAsync(item);

	/// <summary>The row's ⋮ menu: the everyday actions without opening the document.</summary>
	private async Task DocumentMenuAsync(DocumentItem item)
	{
		const string rename = "Đổi tên", export = "Xuất PDF", select = "Chọn nhiều", newFolder = "Tạo thư mục mới và chuyển vào",
			move = "Chuyển vào thư mục...", moveOut = "Chuyển ra ngoài thư mục", delete = "Xoá";
		var actions = new List<string> { rename, export, select, newFolder, move };
		if (item.Record.FolderId != null) actions.Add(moveOut);
		string? choice = await Shell.Current.DisplayActionSheetAsync(item.Name, "Huỷ", delete, [.. actions]);
		switch (choice)
		{
			case rename:
				string? name = await Shell.Current.DisplayPromptAsync("Đổi tên tài liệu", "Tên mới:", "Lưu", "Huỷ",
					initialValue: item.Name, maxLength: 120, keyboard: Keyboard.Text);
				if (name != null && store.Rename(item.Record.Id, name)) RefreshItem(item);
				break;
			case export:
				await exports.ExportAsync(item.Record.Id);
				break;
			case select:
				BeginSelection(item);
				break;
			case newFolder:
				await MoveIntoNewFolderAsync([item.Record.Id]);
				break;
			case move:
				await MoveAsync([item.Record.Id]);
				break;
			case moveOut:
				store.MoveToFolder([item.Record.Id], null);
				await RefreshAsync();
				break;
			case delete:
				await ConfirmDeleteAsync([item]);
				break;
		}
	}

	#endregion

	#region Folders

	/// <summary>The header's back arrow (shown only while <see cref="InFolder"/>): same as the hardware back
	/// button, now that the page draws its own header instead of Shell's (<c>Shell.NavBarIsVisible="False"</c>).</summary>
	[RelayCommand]
	private Task GoBackAsync() => Shell.Current.GoToAsync("..");

	private void OpenFolder(FolderItem folder)
	{
		if (IsSelecting)
		{
			// Tapping a folder while documents are ticked files them there.
			_ = MoveSelectedIntoAsync(folder.Record.Id);
			return;
		}
		_ = Shell.Current.GoToAsync($"{AppShell.Routes.Folder}?folderId={folder.Record.Id}");
	}

	private void ShowFolderMenu(FolderItem folder) => _ = FolderMenuAsync(folder);

	private async Task FolderMenuAsync(FolderItem folder)
	{
		const string rename = "Đổi tên thư mục", move = "Chuyển vào thư mục...", moveOut = "Chuyển ra ngoài thư mục", delete = "Xoá thư mục";
		var actions = new List<string> { rename, move };
		if (folder.Record.ParentFolderId != null) actions.Add(moveOut);
		string? choice = await Shell.Current.DisplayActionSheetAsync(folder.Name, "Huỷ", delete, [.. actions]);
		switch (choice)
		{
			case rename:
				string? name = await Shell.Current.DisplayPromptAsync("Đổi tên thư mục", "Tên mới:", "Lưu", "Huỷ",
					initialValue: folder.Name, maxLength: 80, keyboard: Keyboard.Text);
				if (name != null && store.RenameFolder(folder.Record.Id, name)) await RefreshAsync();
				break;
			case move:
				await MoveFolderAsync(folder);
				break;
			case moveOut:
				if (store.MoveFolder(folder.Record.Id, null)) await RefreshAsync();
				break;
			case delete:
				FolderRecord? parent = folder.Record.ParentFolderId == null ? null : store.Folder(folder.Record.ParentFolderId);
				string destination = parent != null ? $"chuyển ra thư mục \"{parent.Name}\"" : "chuyển ra ngoài cùng";
				bool ok = await Shell.Current.DisplayAlertAsync("Xoá thư mục",
					$"Xoá thư mục \"{folder.Name}\"? Tài liệu và thư mục con bên trong được giữ lại, {destination}.", "Xoá", "Giữ lại");
				if (ok && store.DeleteFolder(folder.Record.Id)) await RefreshAsync();
				break;
		}
	}

	/// <summary>Full path, root first ("Tes / Hợp đồng"), so folders of the same name under different parents
	/// are not indistinguishable in a flat picker like <see cref="MoveAsync"/>'s or this one's.</summary>
	private string FolderPath(FolderRecord f) => string.Join(" / ", store.Ancestors(f.Id).Reverse().Select(a => a.Name).Append(f.Name));

	/// <summary>Asks for a different parent for <paramref name="folder"/> -- any folder anywhere in the tree
	/// except itself and its own descendants (moving into one of those would disconnect that whole branch from
	/// the root; <see cref="DocumentStore.MoveFolder"/> would refuse it too, but the picker simply does not
	/// offer it, so there is nothing to explain when it is missing).</summary>
	private async Task MoveFolderAsync(FolderItem folder)
	{
		const string outside = "Không thư mục (ngoài cùng)";
		var candidates = store.Folders()
			.Where(f => f.Id != folder.Record.Id && !store.Ancestors(f.Id).Any(a => a.Id == folder.Record.Id))
			.ToList();
		var labels = candidates.ToDictionary(f => f, FolderPath);
		var choices = labels.Values.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();
		if (folder.Record.ParentFolderId != null) choices.Add(outside);
		if (choices.Count == 0)
		{
			await Shell.Current.DisplayAlertAsync("Chuyển thư mục", "Không có thư mục nào khác để chuyển vào.", "OK");
			return;
		}
		string? choice = await Shell.Current.DisplayActionSheetAsync($"Chuyển \"{folder.Name}\" vào thư mục", "Huỷ", null, [.. choices]);
		if (choice == null || choice == "Huỷ") return;
		string? target = choice == outside ? null : labels.FirstOrDefault(kv => kv.Value == choice).Key?.Id;
		if (target == null && choice != outside) return;
		if (store.MoveFolder(folder.Record.Id, target)) await RefreshAsync();
	}

	/// <summary>Asks for a folder (an existing one anywhere in the tree, a new one under the folder being
	/// viewed, or none) and files the documents there.</summary>
	private async Task MoveAsync(IReadOnlyList<string> docIds)
	{
		const string create = "+ Thư mục mới...", outside = "Không thư mục (ngoài cùng)";
		IReadOnlyList<FolderRecord> folders = store.Folders();
		var labels = folders.ToDictionary(f => f, FolderPath);
		var choices = labels.Values.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).Append(create).ToList();
		if (_folderId != null) choices.Add(outside);
		string? choice = await Shell.Current.DisplayActionSheetAsync("Chuyển vào thư mục", "Huỷ", null, [.. choices]);
		if (choice == null || choice == "Huỷ") return;
		if (choice == create)
		{
			await MoveIntoNewFolderAsync(docIds);
			return;
		}
		string? target = choice == outside ? null : labels.FirstOrDefault(kv => kv.Value == choice).Key?.Id;
		if (target == null && choice != outside) return;
		store.MoveToFolder(docIds, target);
		EndSelection();
		await RefreshAsync();
	}

	/// <summary>Creates the new folder as a sibling at the level being viewed right now (inside the current
	/// folder, or at the top level), then moves the documents into it.</summary>
	private async Task MoveIntoNewFolderAsync(IReadOnlyList<string> docIds)
	{
		string? name = await Shell.Current.DisplayPromptAsync("Thư mục mới", "Tên thư mục:", "Tạo", "Huỷ",
			placeholder: "Ví dụ: Hợp đồng", maxLength: 80, keyboard: Keyboard.Text);
		if (string.IsNullOrWhiteSpace(name)) return;
		FolderRecord folder = store.CreateFolder(name, _folderId);
		store.MoveToFolder(docIds, folder.Id);
		EndSelection();
		await RefreshAsync();
	}

	/// <summary>"Thư mục mới" on the toolbar: an empty folder, nested under whichever one is being viewed right
	/// now (null -- the top level -- included; any depth).</summary>
	[RelayCommand]
	private async Task NewFolderAsync()
	{
		string? name = await Shell.Current.DisplayPromptAsync("Thư mục mới", "Tên thư mục:", "Tạo", "Huỷ",
			placeholder: "Ví dụ: Hợp đồng", maxLength: 80, keyboard: Keyboard.Text);
		if (string.IsNullOrWhiteSpace(name)) return;
		store.CreateFolder(name, _folderId);
		await RefreshAsync();
	}

	#endregion

	#region Selection and drag

	private void BeginSelection(DocumentItem? first)
	{
		IsSelecting = true;
		foreach (DocumentItem i in Items.OfType<DocumentItem>()) i.IsSelecting = true;
		if (first != null) first.IsSelected = true;
		SelectedCount = Items.OfType<DocumentItem>().Count(i => i.IsSelected);
	}

	[RelayCommand]
	private void EndSelection()
	{
		IsSelecting = false;
		foreach (DocumentItem i in Items.OfType<DocumentItem>())
		{
			i.IsSelecting = false;
			i.IsSelected = false;
		}
		SelectedCount = 0;
		_dragged = null;
	}

	[RelayCommand]
	private void SelectAll()
	{
		foreach (DocumentItem i in Items.OfType<DocumentItem>()) i.IsSelected = true;
		SelectedCount = Items.OfType<DocumentItem>().Count();
	}

	private IReadOnlyList<DocumentItem> Selected() => Items.OfType<DocumentItem>().Where(i => i.IsSelected).ToList();

	[RelayCommand]
	private Task MoveSelectedAsync()
	{
		IReadOnlyList<DocumentItem> items = Selected();
		return items.Count == 0 ? Task.CompletedTask : MoveAsync(items.Select(i => i.Record.Id).ToList());
	}

	[RelayCommand]
	private Task DeleteSelectedAsync() => ConfirmDeleteAsync(Selected());

	private async Task MoveSelectedIntoAsync(string folderId)
	{
		IReadOnlyList<DocumentItem> items = Selected();
		if (items.Count == 0) return;
		store.MoveToFolder(items.Select(i => i.Record.Id), folderId);
		EndSelection();
		await RefreshAsync();
	}

	/// <summary>Long press on a document: selection mode with it ticked, and it (with the other ticked ones) is being
	/// dragged; dropping on a folder files them there.</summary>
	private void StartDrag(DocumentItem item)
	{
		if (!IsSelecting) BeginSelection(item);
		else if (!item.IsSelected)
		{
			item.IsSelected = true;
			SelectedCount = Selected().Count;
		}
		_dragged = item;
	}

	private void DropOnFolder(FolderItem folder)
	{
		if (_dragged == null) return;
		_dragged = null;
		_ = MoveSelectedIntoAsync(folder.Record.Id);
	}

	/// <summary>Back pressed: leave selection / search first.</summary>
	public bool HandleBack()
	{
		if (IsSelecting)
		{
			EndSelection();
			return true;
		}
		if (IsSearching)
		{
			ToggleSearch();
			return true;
		}
		return false;
	}

	#endregion

	/// <summary>The list of exported PDFs.</summary>
	[RelayCommand]
	private Task OpenExportsAsync() => Shell.Current.GoToAsync(AppShell.Routes.Exports);

	[RelayCommand]
	private Task OpenSettingsAsync() => Shell.Current.GoToAsync(AppShell.Routes.Settings);
}
