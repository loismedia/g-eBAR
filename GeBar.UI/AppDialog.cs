using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GeBar.UI;

public sealed class AppDialog : Window
{
    private static readonly SolidColorBrush WarningBackground = AppTheme.Brush("#26FFB300");
    private static readonly SolidColorBrush WarningText = AppTheme.Brush("#FFFFC247");

    private AppDialog(string text, string title, bool warning, string accept, string? cancel)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI Variable Text");
        FontSize = 14;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = AppTheme.ThemeSource });
        AppTheme.Apply(Resources);
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "PrimaryText");

        var heading = new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        UIElement body = message;
        if (warning)
        {
            message.Foreground = WarningText;
            body = new Border { Background = WarningBackground, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 9, 12, 9), Child = message };
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        Button? dismiss = cancel is null ? null : new Button { Content = cancel, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        if (dismiss is not null) buttons.Children.Add(dismiss);
        var primary = new Button { Content = accept, MinWidth = 110, IsDefault = dismiss is null, IsCancel = dismiss is null };
        primary.SetResourceReference(StyleProperty, "PrimaryButton");
        primary.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(primary);

        var layout = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        if (dismiss is not null) layout.Children.Add(heading);
        layout.Children.Add(body);
        layout.Children.Add(buttons);
        Content = layout;
        Loaded += (_, _) =>
        {
            Windows11WindowStyler.Apply(this);
            (dismiss ?? primary).Focus();
        };
    }

    public static void Show(Window? owner, string text, string title, bool warning = false) =>
        new AppDialog(text, title, warning, "OK", null).Run(owner);

    public static bool Confirm(Window? owner, string text, string title, string accept, string cancel) =>
        new AppDialog(text, title, true, accept, cancel).Run(owner);

    private bool Run(Window? owner)
    {
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Topmost = true;
        }
        return ShowDialog() == true;
    }
}
