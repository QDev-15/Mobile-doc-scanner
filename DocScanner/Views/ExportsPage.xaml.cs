using DocScanner.ViewModels;

namespace DocScanner.Views;

public partial class ExportsPage : ContentPage
{
	private readonly ExportsViewModel _viewModel;

	public ExportsPage(ExportsViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = _viewModel = viewModel;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await _viewModel.RefreshAsync();
	}
}
