using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DeadlockScout.App.ViewModels;

/// <summary>Value converters used from XAML as static instances.</summary>
public static class Converters
{
    public static readonly IValueConverter VisibleIfTrue = new Lambda(v => v is true ? Visibility.Visible : Visibility.Collapsed);
    public static readonly IValueConverter CollapsedIfTrue = new Lambda(v => v is true ? Visibility.Collapsed : Visibility.Visible);
    public static readonly IValueConverter VisibleIfNotEmpty = new Lambda(v => v is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed);
    public static readonly IValueConverter VisibleIfNotNull = new Lambda(v => v is not null ? Visibility.Visible : Visibility.Collapsed);
    public static readonly IValueConverter CollapsedIfNotNull = new Lambda(v => v is not null ? Visibility.Collapsed : Visibility.Visible);
    public static readonly IValueConverter VisibleIfPositive = new Lambda(v => v is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed);
    public static readonly IValueConverter BoldIfTrue = new Lambda(v => v is true ? FontWeights.Bold : FontWeights.Normal);

    private static readonly Brush Ahead = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0x8A)));
    private static readonly Brush Behind = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0x7A, 0x6E)));
    private static readonly Brush Even = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));

    /// <summary>Green when 5% or more ahead, red when 5% or more behind, neutral otherwise or without a benchmark.</summary>
    public static readonly IValueConverter DeltaBrush = new Lambda(v => v is double d ? (d >= 0.05 ? Ahead : d <= -0.05 ? Behind : Even) : Even);
    public static readonly IValueConverter LeadBrush = new Lambda(v => v is double d ? (d >= 0.05 ? Behind : d <= -0.05 ? Ahead : Even) : Even); // an enemy ahead is bad news

    /// <summary>
    /// The enemy pace bar: 0 to 54 px for 0 to 30% off the curve (further is drawn full). Only from 5%, where the delta
    /// turns red or green too: a near-even player gets their number but no bar, since a few pixels with rounded ends
    /// read as a stray dot.
    /// </summary>
    public static readonly IValueConverter LeadBar = new Lambda(v => v is double d && Math.Abs(d) >= 0.05 ? Math.Min(Math.Abs(d), 0.30) / 0.30 * 54 : 0.0);

    private static readonly Brush UltimateColor = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0x5D, 0x5D)));
    private static readonly Brush WeaponColor = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0x9A, 0x4A)));
    private static readonly Brush SpiritColor = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0xA8, 0xF8)));
    private static readonly Brush VitalityColor = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0xD8, 0x86)));
    private static readonly Brush OtherColor = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x34)));

    /// <summary>
    /// A spike chip's colour by what the item is for, like the shop's tabs: red for the ultimate, orange for weapon,
    /// blue for spirit, green for vitality (tank, health, sustain); amber when the item list has not loaded.
    /// </summary>
    public static Brush SpikeColor(string? slot) => slot switch
    {
        "ultimate" => UltimateColor,
        "weapon" => WeaponColor,
        "spirit" => SpiritColor,
        "vitality" => VitalityColor,
        _ => OtherColor,
    };

    /// <summary>The quiet chip's fill: its colour at low opacity, so the text in full colour stays readable.</summary>
    public static Brush SpikeFill(string? slot)
    {
        var c = ((SolidColorBrush)SpikeColor(slot)).Color;
        return Freeze(new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B)));
    }

    private static readonly Brush WonBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0x8A)));
    private static readonly Brush LostBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0x7A, 0x6E)));
    /// <summary>The last match card's edge: green for a win, red for a loss.</summary>
    public static readonly IValueConverter OutcomeBrush = new Lambda(v => v is true ? WonBrush : LostBrush);

    private static readonly Brush MeRow = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xE8, 0xB0, 0x4B)));
    /// <summary>Your own row in the scout list, lightly tinted gold.</summary>
    public static readonly IValueConverter MeRowBrush = new Lambda(v => v is true ? MeRow : System.Windows.Media.Brushes.Transparent);

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private sealed class Lambda(Func<object?, object?> convert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
