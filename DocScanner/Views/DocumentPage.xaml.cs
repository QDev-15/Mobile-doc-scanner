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

	/// <summary>The grid has finished a drag-reorder (CanReorderItems: the picked-up page was lifted, the others moved aside and it was
	/// dropped in place); the list already holds the new order, save it.</summary>
	private void OnReorderCompleted(object? sender, EventArgs e) => _viewModel.PagesReordered();
}
