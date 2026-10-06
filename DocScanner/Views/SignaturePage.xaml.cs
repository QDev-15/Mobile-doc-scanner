using System.ComponentModel;
using DocScanner.Core;
using DocScanner.Core.Signatures;
using DocScanner.ViewModels;
using ImageCoreService;
using Microsoft.Maui.Graphics.Platform;
using IImage = Microsoft.Maui.Graphics.IImage;

namespace DocScanner.Views;

/// <summary>Placing signatures on a page. The editor (<see cref="StampEditor"/>) and the drawing pad live here; the view
/// model keeps the saved signatures and stores the result on the page.</summary>
public partial class SignaturePage : ContentPage
{
	/// <summary>Width of a signature just put on the page, as a share of the page's short edge.</summary>
	private const double NewSignatureSize = 0.35;

	private readonly SignatureViewModel _viewModel;
	private readonly IImageService _images;
	private bool _loaded, _changed, _committed;

	public SignaturePage(SignatureViewModel viewModel, IImageService images)
	{
		InitializeComponent();
		BindingContext = _viewModel = viewModel;
		_images = images;
		_viewModel.UseRequested += info => _ = AddAsync(info);
		_viewModel.PropertyChanged += OnViewModelChanged;
		Editor.Changed += () => _changed = true;
		RemoveButton.Command = new Command(() => Editor.RemoveSelected());
		DoneButton.Command = new Command(() => OnDone(this, EventArgs.Empty));
		ClearPadButton.Clicked += (_, _) => Pad.Clear();
		SavePadButton.Clicked += OnSavePad;
		Pad.Ink = InkColor();
	}

	/// <summary>Long edge for a signature picked from a picture: same ceiling as a drawn one
	/// (<see cref="SignatureInk.DefaultMaxEdge"/>), sharp enough printed a third of a page wide at 300 DPI.</summary>
	private const int PickedSignatureMaxEdge = SignatureInk.DefaultMaxEdge;

	private async void OnPickImage(object? sender, EventArgs e)
	{
		try
		{
			FileResult? picked = await MediaPicker.Default.PickPhotoAsync();
			if (picked == null) return; // backed out of the picker

			using Stream stream = await picked.OpenReadAsync();
			string tempPath = Path.Combine(FileSystem.CacheDirectory, $"signature_pick_{Guid.NewGuid():N}.jpg");
			await using (FileStream file = File.Create(tempPath))
				await stream.CopyToAsync(file);

			RgbImage source = await _images.LoadRgbAsync(tempPath, PickedSignatureMaxEdge, default);
			File.Delete(tempPath);

			SignatureInfo info = _viewModel.SaveFromImage(source);
			await AddAsync(info);
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("Không dùng được ảnh này", ex.Message, "OK");
		}
	}

	private Color InkColor() => Color.FromUint(0xFF000000 | _viewModel.InkRgb);

	private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(SignatureViewModel.InkRgb))
		{
			Pad.Ink = InkColor();
			Pad.Invalidate();
		}
		if (e.PropertyName == nameof(SignatureViewModel.IsDrawing) && !_viewModel.IsDrawing) Pad.Clear();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_loaded) return;
		_loaded = true;
		_viewModel.Load();
		await LoadPageAsync();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		// Leaving with the back button keeps what was placed, like "Xong".
		if (!_committed && _changed) Commit();
	}

	private async Task LoadPageAsync()
	{
		PreviewFrame? frame = _viewModel.Frame;
		if (frame == null) return;
		RgbImage picture = frame.Color ?? RgbImage.FromGray(frame.Gray!);
		string file = Path.Combine(FileSystem.CacheDirectory, "signing_page.jpg");
		await _images.SaveJpegAsync(picture, file, 88, default);
		Editor.SetPage(await LoadPictureAsync(file), picture.Width, picture.Height);

		if (_viewModel.Page()?.Stamps is { } stamps)
			foreach (PageStamp s in stamps)
				if (await ItemAsync(s) is { } item) Editor.Items.Add(item);
		Editor.Invalidate();
	}

	private static Task<IImage?> LoadPictureAsync(string path) => Task.Run(() =>
	{
		if (!File.Exists(path)) return null;
		using FileStream fs = File.OpenRead(path);
		return (IImage?)PlatformImage.FromStream(fs);
	});

	private async Task<StampItem?> ItemAsync(PageStamp stamp)
	{
		if (_viewModel.Ink(stamp.SignatureId) is not { } ink) return null;
		IImage? picture = await LoadPictureAsync(_viewModel.ViewPath(stamp.SignatureId));
		return new StampItem(stamp, picture, ink.Mask.Width, ink.Mask.Height);
	}

	private async Task AddAsync(SignatureInfo info)
	{
		// Low on the page, where signatures usually go.
		if (await ItemAsync(new PageStamp(info.Id, 0.5, 0.72, NewSignatureSize)) is { } item) Editor.Add(item);
	}

	private async void OnSavePad(object? sender, EventArgs e)
	{
		if (Pad.IsEmpty)
		{
			await DisplayAlertAsync("Chưa có chữ ký", "Hãy ký vào khung trắng.", "OK");
			return;
		}
		SignatureInfo? info = _viewModel.SaveDrawing(Pad.Strokes(), SignaturePad.PenWidth);
		if (info != null) await AddAsync(info);
	}

	private void Commit()
	{
		_committed = true;
		_viewModel.Commit(Editor.Items.Select(i => i.Stamp).ToList());
		_viewModel.End();
	}

	private async void OnDone(object? sender, EventArgs e)
	{
		Commit();
		await Shell.Current.GoToAsync("..");
	}
}
