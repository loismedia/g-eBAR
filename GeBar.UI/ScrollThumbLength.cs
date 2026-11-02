using System.Globalization;
using System.Windows.Data;

namespace GeBar.UI;

public sealed class ScrollThumbLength : IMultiValueConverter
{
    public double Minimum { get; set; } = 24;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not double viewport) return double.NaN;
        if (values[1] is not double minimum || values[2] is not double maximum || values[3] is not double track) return viewport;
        double range = maximum - minimum;
        if (double.IsNaN(viewport) || range <= 0 || track <= Minimum * 2) return viewport;
        return Math.Max(viewport, Minimum * range / (track - Minimum));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
