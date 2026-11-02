using GeBar.UI;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using WpfButton = System.Windows.Controls.Button;

namespace GeBar.App;

internal partial class WpfColorPickerWindow : Window
{
    private bool _updating;
    public MediaColor SelectedColor { get; private set; }

    public WpfColorPickerWindow(MediaColor initialColor)
    {
        InitializeComponent();
        SelectedColor = initialColor;
        Title = Strings.ChooseColor;
        HexLabel.Text = Strings.HexColor;
        CancelButton.Content = Strings.Cancel;
        OkButton.Content = Strings.Save;
        UpdateControls();
        Loaded += (_, _) => Windows11WindowStyler.Apply(this);
    }

    private void SwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string value } && TryParse(value, out MediaColor color))
        {
            SelectedColor = color;
            UpdateControls();
        }
    }

    private void HexChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !TryParse(HexBox.Text, out MediaColor color)) return;
        SelectedColor = color;
        UpdateControls();
    }

    private void RgbChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || ColorPreview is null) return;
        SelectedColor = MediaColor.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);
        _updating = true;
        HexBox.Text = $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
        ColorPreview.Background = new SolidColorBrush(SelectedColor);
        _updating = false;
    }

    private void OkClick(object sender, RoutedEventArgs e)
    {
        if (!TryParse(HexBox.Text, out MediaColor color))
        {
            AppDialog.Show(this, Strings.InvalidColor, Strings.AppName);
            return;
        }
        SelectedColor = color;
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void UpdateControls()
    {
        _updating = true;
        HexBox.Text = $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
        RedSlider.Value = SelectedColor.R;
        GreenSlider.Value = SelectedColor.G;
        BlueSlider.Value = SelectedColor.B;
        ColorPreview.Background = new SolidColorBrush(SelectedColor);
        _updating = false;
    }

    private static bool TryParse(string value, out MediaColor color)
    {
        try
        {
            object? parsed = MediaColorConverter.ConvertFromString(value.Trim());
            if (parsed is MediaColor result) { color = result; return true; }
        }
        catch (FormatException) { }
        color = default;
        return false;
    }
}
