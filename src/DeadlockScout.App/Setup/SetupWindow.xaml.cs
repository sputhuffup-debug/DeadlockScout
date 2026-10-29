using System.Windows;

namespace DeadlockScout.App.Setup;

/// <summary>
/// The first-run wizard: opens at start-up until finished or skipped (launch.setupDone), and from the tray after that.
/// Six steps; each check has its mark in the list on the left, and the last page sums them up.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly SetupViewModel _vm;
    private readonly Action _calibrate;
    private readonly Action _preview;
    private int _step;

    public SetupWindow(SetupViewModel vm, Action calibrate, Action preview)
    {
        InitializeComponent();
        _vm = vm;
        _calibrate = calibrate;
        _preview = preview;
        DataContext = vm;
        Activated += (_, _) => _vm.Recheck(); // back from Steam, the game or the calibration tool: look again
        ShowStep(0);
    }

    public int StepCount => _vm.Steps.Count;

    /// <summary>Shows step <paramref name="step"/> (also for the screenshot renderer).</summary>
    public void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, StepCount - 1);
        for (int i = 0; i < StepCount; i++)
        {
            if (FindName($"Page{i}") is UIElement page) page.Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
            _vm.Steps[i].Current = i == _step;
        }
        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step == StepCount - 1 ? "Finish" : "Next";
    }

    private void Next(object sender, RoutedEventArgs e)
    {
        if (_step < StepCount - 1) { ShowStep(_step + 1); return; }
        _vm.Finish();
        Close();
    }

    private void Back(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void Skip(object sender, RoutedEventArgs e)
    {
        _vm.Finish();
        Close();
    }

    private void ApplyPaths(object sender, RoutedEventArgs e) => _vm.ApplyPaths();
    private void AddCondebug(object sender, RoutedEventArgs e) => _vm.AddCondebug();
    private void Recheck(object sender, RoutedEventArgs e) => _vm.Recheck();
    private void Calibrate(object sender, RoutedEventArgs e) => _calibrate();
    private void Preview(object sender, RoutedEventArgs e) => _preview();
}
