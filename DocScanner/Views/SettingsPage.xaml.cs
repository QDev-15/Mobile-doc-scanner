using DocScanner.ViewModels;

namespace DocScanner.Views;

public partial class SettingsPage : ContentPage
{
	private readonly SettingsViewModel _viewModel;

	public SettingsPage(SettingsViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = _viewModel = viewModel;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		_viewModel.AttachLicense();
		await _viewModel.LoadStorageAsync();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_viewModel.DetachLicense();
	}
}
