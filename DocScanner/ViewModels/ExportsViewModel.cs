using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocScanner.Core;
using DocScanner.Services;

namespace DocScanner.ViewModels;

/// <summary>"PDF đã xuất": every exported PDF, newest first; tap for open / share / save / delete. A search bar
/// (like the document list's) filters by file name, accents ignored.</summary>
public partial class ExportsViewModel(ExportLibrary library, ExportCoordinator exports) : ObservableObject
{
	/// <summary>Every exported file, unfiltered -- what <see cref="Files"/> is rebuilt from on each refresh or
	/// search-text change, so searching never re-reads the folder.</summary>
	private IReadOnlyList<ExportedFile> _all = [];

	public ObservableCollection<ExportItem> Files { get; } = [];

	[ObservableProperty]
	private string summary = "";

	[ObservableProperty]
	private bool isRefreshing;

	[ObservableProperty]
	private bool isSearching;

	[ObservableProperty]
	private string searchText = "";

	/// <summary>Placeholder text for the list when it is empty, worded for whether that is because nothing has
	/// been exported yet or because the search does not match anything.</summary>
	[ObservableProperty]
	private string emptyText = "";

	partial void OnSearchTextChanged(string value) => ApplyFilter();

	[RelayCommand]
	private void ToggleSearch()
	{
		IsSearching = !IsSearching;
		if (!IsSearching) SearchText = "";
	}

	[RelayCommand]
	public async Task RefreshAsync()
	{
		_all = await Task.Run(library.List);
		ApplyFilter();
		IsRefreshing = false;
	}

	private void ApplyFilter()
	{
		string query = SearchText.Trim();
		IEnumerable<ExportedFile> shown = query.Length == 0 ? _all : _all.Where(f => TextSearch.Matches(f.Name, query));
		Files.Clear();
		foreach (ExportedFile f in shown) Files.Add(new ExportItem(f, ViewAsync, MenuAsync, DeleteAsync));

		Summary = _all.Count == 0 ? "" : $"{_all.Count} file · {Size(_all.Sum(f => f.Bytes))}";
		EmptyText = query.Length > 0 ? $"Không có PDF nào khớp \"{query}\"." : "Mở một tài liệu và bấm \"Xuất PDF\" ở thanh dưới.";
	}

	private static Task ViewAsync(ExportItem item) => ExportCoordinator.ViewAsync(item.File.Path);

	private async Task MenuAsync(ExportItem item)
	{
		bool deleted = await exports.OfferActionsAsync(item.File.Path, item.File.Name, $"{item.File.Name}\n{item.Details}", allowDelete: true);
		if (deleted) await RefreshAsync();
	}

	private async Task DeleteAsync(ExportItem item)
	{
		bool ok = await Shell.Current.DisplayAlertAsync("Xoá PDF", $"Xoá \"{item.File.Name}\"?\n(Tài liệu gốc vẫn còn, có thể xuất lại.)", "Xoá", "Giữ lại");
		if (!ok) return;
		library.Delete(item.File.Path);
		await RefreshAsync();
	}

	public static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}

public sealed class ExportItem
{
	public ExportItem(ExportedFile file, Func<ExportItem, Task> open, Func<ExportItem, Task> menu, Func<ExportItem, Task> delete)
	{
		File = file;
		Details = $"{file.Created:dd/MM/yyyy HH:mm} · {ExportsViewModel.Size(file.Bytes)}";
		OpenCommand = new Command(() => _ = open(this));
		MenuCommand = new Command(() => _ = menu(this));
		DeleteCommand = new Command(() => _ = delete(this));
	}

	public ExportedFile File { get; }
	public string Name => File.Name;
	public string Details { get; }
	public ICommand OpenCommand { get; }
	public ICommand MenuCommand { get; }
	public ICommand DeleteCommand { get; }
}
