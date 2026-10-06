using DocScanner.ViewModels;

namespace DocScanner.Views;

public partial class CropPage : ContentPage
{
	private readonly CropViewModel _viewModel;

	public CropPage(CropViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = _viewModel = viewModel;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_viewModel.Attach();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_viewModel.Detach();
	}
}
