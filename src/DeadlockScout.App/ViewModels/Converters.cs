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
    public static readonly IValueConverter CollapsedIfPositive = new Lambda(v => v is int n && n > 0 ? Visibility.Collapsed : Visibility.Visible);
    /// <summary>"DeadlockScout · Match history" → " · Match history": the title bar draws the name itself, in two colours.</summary>
    public static readonly IValueConverter TitleSuffix = new Lambda(v => v is string t && t.StartsWith("DeadlockScout", StringComparison.Ordinal) ? t["DeadlockScout".Length..] : $" · {v}");
    public static readonly IValueConverter BoldIfTrue = new Lambda(v => v is true ? FontWeights.Bold : FontWeights.Normal);

    private static readonly Brush Ahead = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0x8A)));
    private static readonly Brush Behind = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0x7A, 0x6E)));
    private static readonly Brush Even = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));

    /// <summary>Green when 5% or more ahead, red when 5% or more behind, neutral otherwise or without a benchmark.</summary>
    public static readonly IValueConverter DeltaBrush = new Lambda(v => v is double d ? (d >= 0.05 ? Ahead : d <= -0.05 ? Behind : Even) : Even);
    /// <summary>A target's hit rate: green at 70% or more, red at 40% or less, plain between and when unknown.</summary>
    public static readonly IValueConverter RateBrush = new Lambda(v => v is double r ? (r >= 0.7 ? Ahead : r <= 0.4 ? Behind : Even) : Even);
    /// <summary>An item timing gap in seconds: red when later than the rank by more than 45 s, green when earlier, neutral between.</summary>
    public static readonly IValueConverter TimingBrush = new Lambda(v => v is double s ? (s > 45 ? Behind : s < -45 ? Ahead : Even) : Even);
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
    public static Brush SpikeFill(string? slot, byte alpha = 0x33)
    {
        var c = ((SolidColorBrush)SpikeColor(slot)).Color;
        return Freeze(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));
    }

    private static readonly Brush WonBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0x8A)));
    private static readonly Brush LostBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0x7A, 0x6E)));
    /// <summary>The last match card's edge: green for a win, red for a loss.</summary>
    public static readonly IValueConverter OutcomeBrush = new Lambda(v => v is true ? WonBrush : LostBrush);

    // Party chips: six colours apart from the team colours (amber, sapphire) and the gold accent.
    private static readonly Color[] PartyColors =
    {
        Color.FromRgb(0x4F, 0xD1, 0xC5), // teal
        Color.FromRgb(0xC0, 0x8C, 0xF5), // violet
        Color.FromRgb(0xF6, 0x87, 0xB3), // pink
        Color.FromRgb(0xA3, 0xE0, 0x5C), // lime
        Color.FromRgb(0xF6, 0x8D, 0x55), // orange
        Color.FromRgb(0xE2, 0xE8, 0xF0), // silver
    };
    private static readonly Brush[] PartyText = PartyColors.Select(c => Freeze(new SolidColorBrush(c))).ToArray();
    private static readonly Brush[] PartyBack = PartyColors.Select(c => Freeze(new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B)))).ToArray();
    /// <summary>A likely party's chip text and fill, by its number (1..6), so each party keeps one colour across both lists.</summary>
    public static readonly IValueConverter PartyBrush = new Lambda(v => v is int n && n > 0 ? PartyText[(n - 1) % PartyText.Length] : System.Windows.Media.Brushes.Transparent);
    public static readonly IValueConverter PartyFill = new Lambda(v => v is int n && n > 0 ? PartyBack[(n - 1) % PartyBack.Length] : System.Windows.Media.Brushes.Transparent);

    // The two enemy lines on the pace sparkline: red and violet, apart from your line's green/amber/red and the team colours.
    private static readonly Brush[] RivalColors =
    {
        Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0x7A, 0x6E))),
        Freeze(new SolidColorBrush(Color.FromRgb(0xC0, 0x8C, 0xF5))),
    };
    /// <summary>An enemy line's colour on the sparkline, by its slot (0 or 1).</summary>
    public static readonly IValueConverter RivalBrush = new Lambda(v => v is int i ? RivalColors[Math.Abs(i) % RivalColors.Length] : RivalColors[0]);

    private static readonly Brush WarnText = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x34)));
    private static readonly Brush PlainText = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF2)));
    /// <summary>A diagnostics value worth a look in amber, the rest in the normal text colour.</summary>
    public static readonly IValueConverter WarnBrush = new Lambda(v => v is true ? WarnText : PlainText);

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
