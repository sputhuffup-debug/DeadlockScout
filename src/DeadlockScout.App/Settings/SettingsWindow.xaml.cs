using System.Windows;
using DeadlockScout.App.Services;
using DeadlockScout.Core.Cues;

namespace DeadlockScout.App.Settings;

/// <summary>
/// Every setting in one place, so the main window keeps only its actions: overlay panels and hotkeys, the rank gate,
/// laning timers, objectives, audio cues, the live feed and API key, benchmarks, OCR, and session options. Changes are
/// written to settings.json at once and apply live; "Open settings.json" is still there for anything not shown here.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly AudioCuePlayer? _audio;
    private readonly Action _calibrate;
    private readonly Action _openFile;

    public SettingsWindow(SettingsViewModel vm, AudioCuePlayer? audio, Action calibrate, Action openFile)
    {
        InitializeComponent();
        _vm = vm;
        _audio = audio;
        _calibrate = calibrate;
        _openFile = openFile;
        DataContext = vm;
    }

    /// <summary>The scrolling sections, for the screenshot renderer.</summary>
    public FrameworkElement BodyPart => Body;

    private void Jump(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } && FindName(name) is FrameworkElement section)
        {
            var top = section.TransformToAncestor(Body).Transform(new System.Windows.Point(0, 0)).Y;
            Scroller.ScrollToVerticalOffset(top);
        }
    }

    private void RestoreSpawns(object sender, RoutedEventArgs e) => _vm.RestoreSpawns();

    private async void TestAudio(object sender, RoutedEventArgs e)
    {
        if (_audio is null) return;
        foreach (var cue in new[] { Cue.PushNow, Cue.Objective, Cue.PaceBehind })
        {
            _audio.Play(cue);
            await Task.Delay(1000);
        }
    }

    private void Calibrate(object sender, RoutedEventArgs e) => _calibrate();

    private void OpenFile(object sender, RoutedEventArgs e) => _openFile();
}
