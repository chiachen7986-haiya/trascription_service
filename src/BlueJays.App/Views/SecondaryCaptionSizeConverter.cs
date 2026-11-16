using System.Globalization;
using System.Windows.Data;
namespace BlueJays.App.Views;
public sealed class SecondaryCaptionSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Math.Max(11, (double)value * .6);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
