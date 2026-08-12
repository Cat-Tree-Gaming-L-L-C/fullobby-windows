using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Fullobby.App.Converters;

/// <summary>
/// Resolves and caches the brand brushes used by the converters below. These are declared at the
/// root of <c>Brand.xaml</c> rather than in a theme dictionary — deliberately theme-agnostic — so a
/// resolved brush stays valid for the life of the app and does not need re-looking-up.
/// Without the cache every <c>Convert</c> call did a resource-dictionary lookup plus a cast, and the
/// leaderboard binds four converters per row on every refresh.
/// Converters run only on the UI thread, so the dictionary needs no synchronisation.
/// </summary>
internal static class BrandBrushCache
{
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.Ordinal);

    internal static Brush Get(string key)
    {
        if (!Cache.TryGetValue(key, out var brush))
        {
            brush = (Brush)Application.Current.Resources[key];
            Cache[key] = brush;
        }
        return brush;
    }
}

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

    private static Brush Brush(string key) => BrandBrushCache.Get(key);
}

/// <summary>
/// Marks the active leaderboard period button: returns the accent button style when the bound
/// period equals the <c>ConverterParameter</c>, otherwise <see cref="DependencyProperty.UnsetValue"/>
/// so the button keeps its default style. Without this the selected period is conveyed only by the
/// heading text, so the button row gives no indication of which filter is active.
/// </summary>
public sealed class PeriodToStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var current = value switch { long l => l, int i => i, _ => 0L };
        return parameter is string s && long.TryParse(s, out var target) && current == target
            ? Application.Current.Resources["AccentButtonStyle"]
            : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
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

    private static Brush Brush(string key) => BrandBrushCache.Get(key);
}
