using System.Windows.Input;

namespace DocScanner.Views;

/// <summary>
/// A toolbar button: an icon (Material Icons glyph, see <see cref="Icons"/>) over a small caption, no background, the
/// whole cell tappable. <see cref="IsActive"/> tints it with the brand color (the tool whose panel is open, the chosen
/// filter); a disabled button fades out and ignores taps.
/// </summary>
public class ToolButton : ContentView
{
	public static readonly BindableProperty IconProperty =
		BindableProperty.Create(nameof(Icon), typeof(string), typeof(ToolButton), "", propertyChanged: (b, _, v) => ((ToolButton)b)._icon.Text = (string)v);

	public static readonly BindableProperty TextProperty =
		BindableProperty.Create(nameof(Text), typeof(string), typeof(ToolButton), "", propertyChanged: (b, _, v) => ((ToolButton)b)._caption.Text = (string)v);

	public static readonly BindableProperty CommandProperty =
		BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(ToolButton));

	public static readonly BindableProperty CommandParameterProperty =
		BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(ToolButton));

	public static readonly BindableProperty IsActiveProperty =
		BindableProperty.Create(nameof(IsActive), typeof(bool), typeof(ToolButton), false, propertyChanged: (b, _, _) => ((ToolButton)b).UpdateColors());

	public static readonly BindableProperty IconSizeProperty =
		BindableProperty.Create(nameof(IconSize), typeof(double), typeof(ToolButton), 24.0, propertyChanged: (b, _, v) => ((ToolButton)b)._icon.FontSize = (double)v);

	private readonly Label _icon;
	private readonly Label _caption;

	public ToolButton()
	{
		_icon = new Label
		{
			FontFamily = Icons.FontFamily,
			FontSize = 24,
			HorizontalOptions = LayoutOptions.Center,
		};
		_caption = new Label
		{
			FontSize = 11,
			HorizontalOptions = LayoutOptions.Center,
			HorizontalTextAlignment = TextAlignment.Center,
			MaxLines = 1,
			LineBreakMode = LineBreakMode.TailTruncation,
		};
		Content = new VerticalStackLayout
		{
			Spacing = 2,
			Padding = new Thickness(4, 6),
			VerticalOptions = LayoutOptions.Center,
			Children = { _icon, _caption },
		};
		var tap = new TapGestureRecognizer();
		tap.Tapped += (_, _) =>
		{
			if (!IsEnabled) return;
			if (Command?.CanExecute(CommandParameter) ?? false) Command.Execute(CommandParameter);
		};
		GestureRecognizers.Add(tap);
		MinimumWidthRequest = 56;
		BackgroundColor = Colors.Transparent;
		UpdateColors();
		if (Application.Current != null) Application.Current.RequestedThemeChanged += (_, _) => UpdateColors();
	}

	public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
	public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
	public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
	public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
	public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
	public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

	protected override void OnPropertyChanged(string? propertyName = null)
	{
		base.OnPropertyChanged(propertyName);
		if (propertyName == nameof(IsEnabled)) Opacity = IsEnabled ? 1 : 0.35;
	}

	private void UpdateColors()
	{
		bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
		Color color = IsActive
			? Color.FromArgb(dark ? "#B7ACFF" : "#5B4FE0") // matches Resources/Styles/Colors.xaml's Primary/PrimaryDark
			: Color.FromArgb(dark ? "#C9C2EC" : "#8B86A0");
		_icon.TextColor = color;
		_caption.TextColor = color;
		_caption.FontAttributes = IsActive ? FontAttributes.Bold : FontAttributes.None;
	}
}
