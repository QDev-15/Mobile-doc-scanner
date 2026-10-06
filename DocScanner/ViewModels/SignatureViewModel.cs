using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocScanner.Core;
using DocScanner.Core.Signatures;
using DocScanner.Services;
using ImageCoreService;

namespace DocScanner.ViewModels;

/// <summary>A saved signature in the strip at the bottom of the signature screen.</summary>
public sealed class SignatureChoice(SignatureInfo info, string picture, Action use, Action remove)
{
	public SignatureInfo Info { get; } = info;
	public string Picture { get; } = picture;
	public ICommand UseCommand { get; } = new Command(use);
	public ICommand RemoveCommand { get; } = new Command(remove);
}

/// <summary>
/// Signing a page: the saved signatures (tap one to put it on the page; draw a new one), and the signatures on the page,
/// placed with <c>StampEditor</c> by the view. "Xong" stores them on the page, which is then rendered with them.
/// </summary>
public partial class SignatureViewModel(SignatureLibrary library, PageEditService edit, DocumentStore store, SignatureSession session)
	: ObservableObject
{
	/// <summary>Ink colors offered for a new signature: black, blue (the usual pen), dark red.</summary>
	public static readonly (string Name, uint Rgb)[] Inks = [("Đen", 0x111111), ("Xanh", 0x1A3A9E), ("Đỏ", 0xB71C1C)];

	public ObservableCollection<SignatureChoice> Saved { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSaved))]
	private int savedCount;

	public bool HasSaved => SavedCount > 0;

	/// <summary>The drawing pad is open.</summary>
	[ObservableProperty]
	private bool isDrawing;

	[ObservableProperty]
	private int inkIndex = 1;

	public uint InkRgb => Inks[InkIndex].Rgb;

	partial void OnInkIndexChanged(int value) => OnPropertyChanged(nameof(InkRgb));

	/// <summary>Put a saved signature on the page (handled by the view: it owns the editor).</summary>
	public event Action<SignatureInfo>? UseRequested;

	/// <summary>The page to sign and its picture (without its signatures), or null when the screen was opened wrongly.</summary>
	public PreviewFrame? Frame => session.Frame;

	public PageRecord? Page() => session.DocId == null ? null
		: store.Pages(session.DocId).FirstOrDefault(p => p.Id == session.PageId);

	public void Load()
	{
		Saved.Clear();
		foreach (SignatureInfo info in library.List()) Saved.Add(Choice(info));
		SavedCount = Saved.Count;
		// No signature yet: go straight to drawing one.
		if (Saved.Count == 0) IsDrawing = true;
	}

	private SignatureChoice Choice(SignatureInfo info)
	{
		SignatureChoice? self = null;
		self = new SignatureChoice(info, library.ViewPath(info.Id), () => UseRequested?.Invoke(info), () => _ = RemoveAsync(self!));
		return self;
	}

	public SignatureInkImage? Ink(string id) => library.Ink(id);
	public string ViewPath(string id) => library.ViewPath(id);

	private async Task RemoveAsync(SignatureChoice choice)
	{
		bool ok = await Shell.Current.DisplayAlertAsync("Xoá chữ ký đã lưu",
			"Xoá chữ ký này khỏi danh sách? Các trang đã ký bằng nó sẽ không còn chữ ký đó.", "Xoá", "Giữ lại");
		if (!ok) return;
		library.Delete(choice.Info.Id);
		Saved.Remove(choice);
		SavedCount = Saved.Count;
	}

	[RelayCommand]
	private void StartDrawing() => IsDrawing = true;

	[RelayCommand]
	private void CancelDrawing() => IsDrawing = false;

	[RelayCommand]
	private void SetInk(string index) => InkIndex = int.Parse(index);

	/// <summary>Saves a drawn signature (strokes in dp) and returns it, or null when nothing was drawn.</summary>
	public SignatureInfo? SaveDrawing(IReadOnlyList<IReadOnlyList<PointD>> strokes, double penWidth)
	{
		GrayImage? mask = SignatureInk.Rasterize(strokes, penWidth);
		if (mask == null) return null;
		SignatureInfo info = library.Add(mask, InkRgb);
		Saved.Insert(0, Choice(info));
		SavedCount = Saved.Count;
		IsDrawing = false;
		return info;
	}

	/// <summary>Saves a signature picked from an existing picture on the device (a photo of a signature on
	/// paper, or an already-cut-out signature image) - same ink-mask storage as a drawn one.</summary>
	public SignatureInfo SaveFromImage(RgbImage source)
	{
		GrayImage mask = SignatureImageImport.ToMask(source);
		SignatureInfo info = library.Add(mask, InkRgb);
		Saved.Insert(0, Choice(info));
		SavedCount = Saved.Count;
		return info;
	}

	/// <summary>Stores the signatures on the page (the page is rendered again with them).</summary>
	public void Commit(IReadOnlyList<PageStamp> stamps)
	{
		if (session.DocId == null || session.PageId == null) return;
		edit.SetStamps(session.DocId, session.PageId, stamps);
	}

	public void End() => session.End();
}
