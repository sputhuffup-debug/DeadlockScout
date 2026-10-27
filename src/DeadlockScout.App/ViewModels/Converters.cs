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
    public static readonly IValueConverter VisibleIfPositive = new Lambda(v => v is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed);
    public static readonly IValueConverter BoldIfTrue = new Lambda(v => v is true ? FontWeights.Bold : FontWeights.Normal);

    private static readonly Brush Ahead = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0x8A)));
    private static readonly Brush Behind = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0x7A, 0x6E)));
    private static readonly Brush Even = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));

    /// <summary>Green when 5% or more ahead, red when 5% or more behind, neutral otherwise or without a benchmark.</summary>
    public static readonly IValueConverter DeltaBrush = new Lambda(v => v is double d ? (d >= 0.05 ? Ahead : d <= -0.05 ? Behind : Even) : Even);
    public static readonly IValueConverter LeadBrush = new Lambda(v => v is double d ? (d >= 0.05 ? Behind : d <= -0.05 ? Ahead : Even) : Even); // an enemy ahead is bad news

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private sealed class Lambda(Func<object?, object?> convert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
