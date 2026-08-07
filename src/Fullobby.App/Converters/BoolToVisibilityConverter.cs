using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Fullobby.App.Converters;

/// <summary>
/// Maps a bool to <see cref="Visibility"/>: true → Visible, false → Collapsed.
/// Pass <c>ConverterParameter="invert"</c> to flip the mapping.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
