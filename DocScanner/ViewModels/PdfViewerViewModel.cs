using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocScanner.Services;

namespace DocScanner.ViewModels;

/// <summary>An exported PDF as one continuous, pinch-zoomable scroll through every page (<c>PdfScrollView</c>,
/// hosted by <c>PdfViewerPage</c>) -- this view model only tracks which page is topmost, for the small page
/// indicator; the scrolling and zooming themselves are native and never go through data binding.</summary>
public partial class PdfViewerViewModel(ExportCoordinator exports) : ObservableObject, IQueryAttributable
{
	[ObservableProperty]
	private string title = "";

	[ObservableProperty]
	private string pageText = "";

	[ObservableProperty]
	private bool hasPage;

	public string? Path { get; private set; }

	/// <summary>A new file was opened (the view opens its renderer).</summary>
	public event Action<string>? FileChanged;

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		if (!query.TryGetValue("path", out object? p) || p is not string path) return;
		Path = Uri.UnescapeDataString(path);
		Title = System.IO.Path.GetFileNameWithoutExtension(Path);
		PageText = "";
		HasPage = false;
		FileChanged?.Invoke(Path);
	}

	/// <summary>The topmost visible page changed (reported by the native scroll view as it scrolls); count = 0
	/// means the file could not be opened.</summary>
	public void ReportVisiblePage(int index, int count)
	{
		HasPage = count > 0;
		PageText = count > 0 ? $"Trang {index + 1}/{count}" : "Không mở được file";
	}

	/// <summary>Share / save / open with another app / delete.</summary>
	[RelayCommand]
	private async Task MoreAsync()
	{
		if (Path == null) return;
		bool deleted = await exports.OfferActionsAsync(Path, Title, Title, allowDelete: true, offerView: false);
		if (deleted) await Shell.Current.GoToAsync("..");
	}
}
