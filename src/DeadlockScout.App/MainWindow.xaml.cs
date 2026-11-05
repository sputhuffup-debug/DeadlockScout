using System.Windows;
using DeadlockScout.App.Services;
using DeadlockScout.App.ViewModels;

namespace DeadlockScout.App;

/// <summary>
/// Shows the same panels as the overlay without the game running, plus setup actions; detection details are on the
/// diagnostics page. A view only: what its buttons do lives in <see cref="AppShell"/>, which builds this window when it
/// is opened and lets it go when it is closed to the tray, so a closed window holds no memory through a match.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppShell _shell;

    public MainWindow(AppShell shell, OverlayState state)
    {
        InitializeComponent();
        _shell = shell;
        DataContext = state;
        ShowPreview(shell.PreviewOn);
        shell.PreviewChanged += ShowPreview;
        Closed += (_, _) => shell.PreviewChanged -= ShowPreview; // the shell outlives this window
    }

    private void ShowPreview(bool on) => PreviewButton.Content = on ? "Stop preview" : "Preview overlay";

    private void ExitApp(object sender, RoutedEventArgs e) => _shell.Exit();
    private void ToggleOverlay(object sender, RoutedEventArgs e) => _shell.ToggleOverlay();
    private void PreviewOverlay(object sender, RoutedEventArgs e) => _shell.TogglePreview();
    private void MatchHistory(object sender, RoutedEventArgs e) => _shell.OpenMatchHistory();
    private void OpenDiagnostics(object sender, RoutedEventArgs e) => _shell.OpenDiagnosticsWindow();
    private void OpenSettings(object sender, RoutedEventArgs e) => _shell.OpenSettingsWindow();
    private void AddCondebug(object sender, RoutedEventArgs e) => _shell.AddCondebug();
    private void ShowDemo(object sender, RoutedEventArgs e) => _shell.ToggleDemo();

    /// <summary>The last match's share card on the clipboard; the button says for a moment whether it worked.</summary>
    private async void CopyShareCard(object sender, RoutedEventArgs e)
    {
        bool ok = DataContext is OverlayState s && Services.ShareCard.Copy(s.LastMatch);
        ShareButton.Content = ok ? "Copied" : "Could not copy";
        await Task.Delay(TimeSpan.FromSeconds(2));
        ShareButton.Content = "Copy as image";
    }
}
