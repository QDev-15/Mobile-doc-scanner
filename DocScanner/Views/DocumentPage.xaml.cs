using DocScanner.ViewModels;

namespace DocScanner.Views;

public partial class DocumentPage : ContentPage
{
	private readonly DocumentViewModel _viewModel;

	public DocumentPage(DocumentViewModel viewModel)
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
