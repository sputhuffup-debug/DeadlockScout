using System.Windows.Forms;

namespace DeadlockScout.App.Services;

/// <summary>The app lives in the tray; this is its only always-visible UI.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private ToolStripMenuItem? _update;

    public TrayIcon(Action showMain, Action toggleOverlay, Action toggleScout, Action previewOverlay, Action matchHistory, Action calibrateOcr, Action exit, string scoutHotkey,
                    Action? settings = null, Action? setup = null, Action? diagnostics = null)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open DeadlockScout", null, (_, _) => showMain());
        menu.Items.Add("Toggle overlay", null, (_, _) => toggleOverlay());
        menu.Items.Add($"Lobby stats ({scoutHotkey})", null, (_, _) => toggleScout());
        menu.Items.Add("Preview overlay (on/off)", null, (_, _) => previewOverlay());
        menu.Items.Add("Match history", null, (_, _) => matchHistory());
        menu.Items.Add("Calibrate OCR regions", null, (_, _) => calibrateOcr());
        if (settings is not null) menu.Items.Add("Settings", null, (_, _) => settings());
        if (setup is not null) menu.Items.Add("Setup wizard", null, (_, _) => setup());
        if (diagnostics is not null) menu.Items.Add("Diagnostics", null, (_, _) => diagnostics());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "DeadlockScout",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => showMain();
    }

    /// <summary>
    /// Puts "Update to 1.2.3" at the top of the menu (or renames it for a newer one) and says so once in a balloon.
    /// </summary>
    public void ShowUpdate(string version, Action install)
    {
        var menu = _icon.ContextMenuStrip!;
        if (_update is null)
        {
            _update = new ToolStripMenuItem { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) };
            _update.Click += (_, _) => install();
            menu.Items.Insert(0, _update);
            menu.Items.Insert(1, new ToolStripSeparator());
        }
        _update.Text = $"Update to {version}";
        Notify("Update available", $"DeadlockScout {version} is ready. Install it from the tray menu between matches.");
    }

    /// <summary>The app icon at the tray's size (16 px at 100% scaling, 20/24/32 px above), else the generic one.</summary>
    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info is not null)
                using (var stream = info.Stream)
                    return new System.Drawing.Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception) { }
        return System.Drawing.SystemIcons.Application;
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
