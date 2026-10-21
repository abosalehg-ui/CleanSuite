using System.Globalization;
using System.Windows.Data;

namespace CleanSuite.App.Theming;

/// <summary>true ↔ false (e.g. IsEnabled="{Binding IsBusy, Converter={StaticResource Not}}").</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
