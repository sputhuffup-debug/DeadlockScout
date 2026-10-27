using System.Windows.Forms;

namespace DeadlockScout.App.Services;

/// <summary>The app lives in the tray; this is its only always-visible UI.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action showMain, Action toggleOverlay, Action calibrateOcr, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open DeadlockScout", null, (_, _) => showMain());
        menu.Items.Add("Toggle overlay", null, (_, _) => toggleOverlay());
        menu.Items.Add("Calibrate OCR regions", null, (_, _) => calibrateOcr());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "DeadlockScout",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => showMain();
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
