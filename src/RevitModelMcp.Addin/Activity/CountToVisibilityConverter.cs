using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RevitModelMcp.Activity;

/// <summary>Collapses an element when a bound integer count is zero.</summary>
internal sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
