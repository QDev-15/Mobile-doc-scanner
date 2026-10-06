using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DocScanner.Core;

namespace DocScanner.ViewModels;

/// <summary>A row of the main screen: a folder or a document.</summary>
public abstract partial class HomeItem : ObservableObject
{
	[ObservableProperty]
	private string name = "";

	[ObservableProperty]
	private string subtitle = "";
}

/// <summary>A folder row: tap to open it, drop documents on it to file them there, â‹® for rename / delete.</summary>
public sealed class FolderItem : HomeItem
{
	/// <param name="subfolders">Direct sub-folders, shown alongside the document count so a folder that holds
	/// only sub-folders (no documents of its own) does not read as empty.</param>
	public FolderItem(FolderRecord record, int count, int subfolders, Action<FolderItem> open, Action<FolderItem> menu, Action<FolderItem> drop)
	{
		Record = record;
		Name = record.Name;
		Subtitle = subfolders == 0 ? $"{count} tài liệu" : $"{subfolders} thư mục con, {count} tài liệu";
		OpenCommand = new Command(() => open(this));
		MenuCommand = new Command(() => menu(this));
		DropCommand = new Command(() => drop(this));
	}

	public FolderRecord Record { get; }
	public ICommand OpenCommand { get; }
	public ICommand MenuCommand { get; }
	public ICommand DropCommand { get; }
}

/// <summary>Row of the document list. Its thumbnail and page counts follow the background
/// pipeline (see <see cref="Refresh"/>). In selection mode a tap ticks it instead of opening it.</summary>
public partial class DocumentItem : HomeItem
{
	private string? _thumbPath;

	public DocumentItem(DocumentRecord record, Action<DocumentItem> open, Action<DocumentItem> delete, Action<DocumentItem>? menu = null,
		Action<DocumentItem>? dragStart = null)
	{
		Record = record;
		Name = record.Name;
		OpenCommand = new Command(() => open(this));
		DeleteCommand = new Command(() => delete(this));
		MenuCommand = new Command(() => menu?.Invoke(this));
		DragStartingCommand = new Command(() => dragStart?.Invoke(this));
	}

	/// <summary>Ticked in selection mode.</summary>
	[ObservableProperty]
	private bool isSelected;

	/// <summary>The list is in selection mode (the tick boxes show).</summary>
	[ObservableProperty]
	private bool isSelecting;

	/// <summary>Long press: start dragging (into a folder); also enters selection mode.</summary>
	public ICommand DragStartingCommand { get; }

	public DocumentRecord Record { get; }

	public ICommand OpenCommand { get; }
	public ICommand DeleteCommand { get; }
	/// <summary>The row's ⋮ menu (rename, export, delete).</summary>
	public ICommand MenuCommand { get; }

	[ObservableProperty]
	private ImageSource? thumb;

	/// <summary>Bare page count, for the pill badge next to the row (Subtitle already folds this into a sentence;
	/// the badge wants just the number).</summary>
	[ObservableProperty]
	private string pageCountText = "";

	/// <param name="pages">Snapshot of the document's pages.</param>
	/// <param name="firstThumbPath">Where the first page's thumbnail will be, once it exists.</param>
	/// <param name="import">The document's background import, if any.</param>
	public void Refresh(IReadOnlyList<PageRecord> pages, string? firstThumbPath, ImportStatus? import = null)
	{
		int busy = pages.Count(p => p.State is PageState.Pending or PageState.Preview);
		string date = Record.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
		string activity = import is { Running: true } ? $" · đang nhập {import.Done}/{import.Total}"
			: busy > 0 ? $" · đang xử lý {busy}" : "";
		Subtitle = $"{pages.Count} trang{activity} · {date}";
		PageCountText = pages.Count.ToString();

		Name = Record.Name; // may have been renamed
		if (firstThumbPath != null && firstThumbPath != _thumbPath && File.Exists(firstThumbPath))
		{
			Thumb = ImageSource.FromFile(firstThumbPath);
			_thumbPath = firstThumbPath;
		}
	}
}

/// <summary>Tile of the page grid inside a document. The tile exists from the moment the page is
/// added (a spinner), shows the thumbnail as soon as it has been made, and marks a photo that
/// could not be read.</summary>
public partial class PageItem : ObservableObject
{
	private readonly Func<PageRecord, string> _thumbPath;
	private readonly Func<string, double?> _copyProgress;
	private readonly Func<string, bool> _preparing;
	private string? _loadedKey;

	/// <param name="thumbPath">Which file to show for the page (the straightened thumbnail once there is a
	/// current one, otherwise the plain thumbnail).</param>
	/// <param name="copyProgress">Share of the page's photo copied so far, while it is being imported.</param>
	/// <param name="preparing">True while the page still has background work before it can be edited.</param>
	public PageItem(PageRecord record, int number, Func<PageRecord, string> thumbPath, Action<PageItem> open, Action<PageItem> delete,
		Action<PageItem>? menu = null, Action<PageItem>? dragStart = null, Action<PageItem>? drop = null,
		Func<string, double?>? copyProgress = null, Func<string, bool>? preparing = null)
	{
		Record = record;
		_thumbPath = thumbPath;
		_copyProgress = copyProgress ?? (_ => null);
		_preparing = preparing ?? (_ => false);
		SetNumber(number);
		OpenCommand = new Command(() => open(this));
		DeleteCommand = new Command(() => delete(this));
		MenuCommand = new Command(() => menu?.Invoke(this));
		DragStartingCommand = new Command(() => dragStart?.Invoke(this));
		DropCommand = new Command(() => drop?.Invoke(this));
		Refresh();
	}

	public PageRecord Record { get; }
	/// <summary>"Trang n": renumbered in place when pages are moved or deleted.</summary>
	[ObservableProperty]
	private string label = "";

	/// <summary>The bare number, for the badge on the tile.</summary>
	[ObservableProperty]
	private string numberText = "";

	public void SetNumber(int number)
	{
		Label = $"Trang {number}";
		NumberText = number.ToString();
	}
	public ICommand OpenCommand { get; }
	public ICommand DeleteCommand { get; }
	/// <summary>Page actions (move, delete...).</summary>
	public ICommand MenuCommand { get; }
	/// <summary>Drag to reorder: this tile is picked up / another tile is dropped on it.</summary>
	public ICommand DragStartingCommand { get; }
	public ICommand DropCommand { get; }

	[ObservableProperty]
	private ImageSource? thumb;

	[ObservableProperty]
	private bool showThumb;

	/// <summary>No picture yet: the tile shows "Đang tải..." with a spinner in its place.</summary>
	[ObservableProperty]
	private bool showPlaceholder;

	[ObservableProperty]
	private bool isFailed;

	[ObservableProperty]
	private string statusText = "";

	/// <summary>0..1 through the page's preparation: copying the photo, thumbnail, screen copy, paper outline.</summary>
	[ObservableProperty]
	private double progress;

	[ObservableProperty]
	private bool showProgress;

	/// <summary>The page number badge (hidden while the progress strip is shown).</summary>
	[ObservableProperty]
	private bool showNumber;

	/// <summary>What the page is waiting for ("Đang tải 40%", "Đang dò mép giấy"...).</summary>
	[ObservableProperty]
	private string stageText = "";

	/// <summary>Re-reads the page state (called when the import or the pipeline reports a change). Only properties that
	/// really changed notify the view, so a tile updates in place without disturbing the list.</summary>
	public void Refresh()
	{
		PageState state = Record.State;
		IsFailed = state == PageState.Failed;
		StatusText = IsFailed ? "Không đọc được ảnh" : "";

		if (state is PageState.Pending or PageState.Importing)
		{
			Thumb = null; // being (re)built: do not keep showing the old picture
			_loadedKey = null;
		}
		else if (state is PageState.Preview or PageState.Ready)
		{
			// Reload when the file, the rotation or the render changes (the plain thumbnail keeps its name).
			string path = _thumbPath(Record);
			string key = $"{path}|{Record.UserRotation}|{Record.CroppedRevision}";
			if (key != _loadedKey && File.Exists(path))
			{
				Thumb = ImageSource.FromFile(path);
				_loadedKey = key;
			}
		}

		(double progress, string stage, bool working) = state switch
		{
			PageState.Importing when _copyProgress(Record.Id) is double f => (0.05 + 0.45 * f, $"Đang tải {f:P0}", true),
			PageState.Importing => (0.0, "Đang chờ tải...", true),
			PageState.Pending => (0.55, "Đang tạo ảnh xem trước", true),
			PageState.Preview => (0.75, "Đang xử lý ảnh", true),
			PageState.Ready when Record.CropQuad == null && _preparing(Record.Id) => (0.9, "Đang dò mép giấy", true),
			_ => (1.0, "", false),
		};
		Progress = progress;
		StageText = stage;
		ShowProgress = working;
		ShowNumber = !working;

		ShowThumb = Thumb != null && !IsFailed;
		ShowPlaceholder = Thumb == null && !IsFailed;
	}
}
