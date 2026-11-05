using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeadlockScout.App.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace DeadlockScout.App.Services;

/// <summary>
/// The last match's report as a picture to paste into Discord. Only your own numbers: the result line (hero, outcome,
/// length, K/D/A, souls), the pace, the lane line (which names the opponent's hero, never a player), the phases and the
/// spending recap. Never the footer (match id and start time), no other player's name, account id or rank, and no game
/// art: the card is drawn from text on the app's own colours.
/// </summary>
public static class ShareCard
{
    public const double Width = 560;

    /// <summary>The lines on the card, in order; empty ones are left out. Pure over the report's text, so the content is plain.</summary>
    public static IReadOnlyList<string> Lines(PostMatchViewModel m)
        => new[] { m.Title, m.Pace, m.Lane, m.Phases, m.Recap }.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

    /// <summary>The card at twice its size, for a sharp paste.</summary>
    public static BitmapSource Render(PostMatchViewModel m)
    {
        Brush Res(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;
        var fg = Res("Fg", Brushes.White);
        var muted = Res("Muted", Brushes.Gray);
        var gold = Res("Gold", Brushes.Goldenrod);
        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        stack.Children.Add(new TextBlock { Text = "LAST MATCH", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = muted });
        var lines = Lines(m);
        for (int i = 0; i < lines.Count; i++)
            stack.Children.Add(new TextBlock
            {
                Text = lines[i], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, i == 0 ? 2 : 3, 0, 0),
                FontSize = i == 0 ? 16 : 13, FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = lines[i] == m.Recap ? gold : fg,
            });
        stack.Children.Add(new TextBlock { Text = "DeadlockScout", FontSize = 11, Foreground = muted, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right });
        var card = new Border
        {
            Width = Width,
            Background = Res("Surface", Brushes.Black),
            BorderBrush = m.Won ? Res("Good", Brushes.LimeGreen) : Res("Bad", Brushes.IndianRed),
            BorderThickness = new Thickness(5, 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Child = stack,
        };
        card.Measure(new System.Windows.Size(Width, double.PositiveInfinity));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth * 2), (int)Math.Ceiling(card.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(card);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Puts the card on the clipboard; false when there is no report or the clipboard is busy.</summary>
    public static bool Copy(PostMatchViewModel m)
    {
        if (!m.HasData) return false;
        try { Clipboard.SetImage(Render(m)); return true; }
        catch (System.Runtime.InteropServices.COMException) { return false; } // another program holds the clipboard
    }

    public static void Save(BitmapSource image, string path)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        png.Save(file);
    }
}
