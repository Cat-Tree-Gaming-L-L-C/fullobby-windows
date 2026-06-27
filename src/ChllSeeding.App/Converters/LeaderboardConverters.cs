using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace ChllSeeding.App.Converters;

/// <summary>
/// Maps a leaderboard rank to its medal text colour: 1 → gold, 2 → silver, 3 → bronze.
/// Any other rank returns <see cref="DependencyProperty.UnsetValue"/> so the binding falls
/// back to the default text brush.
/// </summary>
public sealed class RankToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        ToRank(value) switch
        {
            1 => Brush("BrandRank1Brush"),
            2 => Brush("BrandRank2Brush"),
            3 => Brush("BrandRank3Brush"),
            _ => DependencyProperty.UnsetValue,
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static long ToRank(object value) => value switch
    {
        long l => l,
        int i => i,
        _ => 0,
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>
/// Maps a leaderboard rank to a font weight: 1 → bold, 2/3 → semibold, else normal.
/// Mirrors the <c>font-bold</c> / <c>font-semibold</c> classes on the top three rows.
/// </summary>
public sealed class RankToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (value switch { long l => l, int i => i, _ => 0L }) switch
        {
            1 => FontWeights.Bold,
            2 or 3 => FontWeights.SemiBold,
            _ => FontWeights.Normal,
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a session status string to its badge fill: "completed" → success (green),
/// "active" → info (blue), anything else → warning (amber). Port of the
/// <c>badge_class</c> match in the leaderboard "Recent Sessions" list.
/// </summary>
public sealed class SessionStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (value as string) switch
        {
            "completed" => Brush("BrandBadgeSuccessBrush"),
            "active" => Brush("BrandBadgeInfoBrush"),
            _ => Brush("BrandBadgeWarningBrush"),
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
