using System.Windows;
using System.Windows.Media;

namespace GeBar.UI;

public static class AppTheme
{
    public const string AccentColor = "#00D2D2";
    public const string AccentTextColor = "#101010";

    public static readonly Uri ThemeSource = new("pack://application:,,,/GeBar.UI;component/Theme.xaml", UriKind.Absolute);

    public static void Apply(ResourceDictionary resources)
    {
        bool dark = Windows11WindowStyler.IsDarkTheme();
        resources["WindowBackground"] = Brush(dark ? "#202020" : "#F3F3F3");
        resources["CardBackground"] = Brush(dark ? "#2B2B2B" : "#FFFFFF");
        resources["ControlBackground"] = Brush(dark ? "#333333" : "#F8F8F8");
        resources["ControlHover"] = Brush(dark ? "#414141" : "#E9E9E9");
        resources["BorderBrush"] = Brush(dark ? "#484848" : "#D7D7D7");
        resources["DividerBrush"] = Brush(dark ? "#3A3A3A" : "#E5E5E5");
        resources["PrimaryText"] = Brush(dark ? "#F5F5F5" : "#1B1B1B");
        resources["SecondaryText"] = Brush(dark ? "#B9B9B9" : "#666666");
        resources["AccentBrush"] = Brush(AccentColor);
        resources["AccentTextBrush"] = Brush(AccentTextColor);
    }

    public static void ApplyTo(Window window)
    {
        Apply(window.Resources);
        if (window.IsLoaded) Windows11WindowStyler.Apply(window);
    }

    public static SolidColorBrush Brush(string value) => Brush((Color)ColorConverter.ConvertFromString(value));

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
