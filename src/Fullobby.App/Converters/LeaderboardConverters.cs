using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Fullobby.App.Converters;

/// <summary>
/// Resolves and caches the app-level resources used by the converters below. The brand brushes and
/// medal styles are declared at the root of <c>Brand.xaml</c> rather than in a theme dictionary —
/// deliberately theme-agnostic — so a resolved value stays valid for the life of the app and does
/// not need re-looking-up. (Anything that varies per theme must NOT be cached here: the lookup
/// would freeze one theme's value and survive a theme switch.)
/// Without the cache every <c>Convert</c> call did a resource-dictionary lookup plus a cast, and the
/// leaderboard binds a converter per row on every refresh.
/// Converters run only on the UI thread, so the dictionary needs no synchronisation.
/// </summary>
internal static class BrandResourceCache
{
    private static readonly Dictionary<string, object> Cache = new(StringComparer.Ordinal);

    internal static T Get<T>(string key)
    {
        if (!Cache.TryGetValue(key, out var value))
        {
            value = Application.Current.Resources[key];
            Cache[key] = value;
        }
        return (T)value;
    }
}

/// <summary>
/// Maps a leaderboard rank to its medal row style: 1 → gold/bold, 2 → silver/semibold,
/// 3 → bronze/semibold. Any other rank returns <c>null</c>, leaving the row on the default
/// TextBlock style.
/// </summary>
/// <remarks>
/// Colour and weight are carried by one <see cref="Style"/> instead of two bound properties, and
/// the fallback is null rather than <see cref="DependencyProperty.UnsetValue"/>, because
/// <c>{x:Bind}</c> hard-casts a converter's result to the target type. UnsetValue is an
/// <c>IInspectable</c> sentinel, so that cast threw <see cref="InvalidCastException"/> and took the
/// process down as soon as a rank outside the top three rendered. Unlike <c>{Binding}</c>, compiled
/// bindings have no "leave the property alone" value — only a real instance or null.
/// </remarks>
public sealed class RankToStyleConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language) =>
        (value switch { long l => l, int i => i, _ => 0L }) switch
        {
            1 => Style("LeaderboardRank1TextStyle"),
            2 => Style("LeaderboardRank2TextStyle"),
            3 => Style("LeaderboardRank3TextStyle"),
            _ => null,
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static Style Style(string key) => BrandResourceCache.Get<Style>(key);
}

/// <summary>
/// Marks the active leaderboard period button: returns the accent button style when the bound
/// period equals the <c>ConverterParameter</c>, otherwise <c>null</c> so the button falls back to
/// the default style. Without this the selected period is conveyed only by the heading text, so the
/// button row gives no indication of which filter is active.
/// </summary>
/// <remarks>
/// Returns null, not <see cref="DependencyProperty.UnsetValue"/> — see
/// <see cref="RankToStyleConverter"/> for why UnsetValue crashes a compiled binding.
/// </remarks>
public sealed class PeriodToStyleConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var current = value switch { long l => l, int i => i, _ => 0L };
        return parameter is string s && long.TryParse(s, out var target) && current == target
            ? BrandResourceCache.Get<Style>("AccentButtonStyle")
            : null;
    }

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

    private static Brush Brush(string key) => BrandResourceCache.Get<Brush>(key);
}
