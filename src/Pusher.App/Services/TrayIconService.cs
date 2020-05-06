using System.Drawing;
using System.IO;
using System.Windows;
using Pusher.Storage;
using WinForms = System.Windows.Forms;

namespace Pusher.App.Services;

/// <summary>
/// System-tray integration: closing or minimizing the window hides it to the tray so
/// scheduled pushes keep being monitored; the tray menu restores or fully exits.
/// Behavior (close-to-tray, minimize-to-tray, notifications) is user-configurable
/// in Settings. Uses the WinForms NotifyIcon (no extra NuGet dependency).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Window _window;
    private readonly AppSettingsStore _settings;
    private readonly WinForms.NotifyIcon _icon;
    private bool _balloonShown;
    private bool _reallyExit;

    public TrayIconService(Window window, AppSettingsStore settings)
    {
        _window = window;
        _settings = settings;

        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        _icon = new WinForms.NotifyIcon
        {
            Icon = File.Exists(icoPath) ? new Icon(icoPath) : SystemIcons.Application,
            Text = "GitHub Schedule Pusher",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Schedule Pusher", null, (_, _) => Restore());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => Restore();

        // X button hides to tray instead of exiting (configurable in Settings).
        _window.Closing += (_, e) =>
        {
            if (_reallyExit || !_settings.Load().CloseToTray) return;
            e.Cancel = true;
            HideToTray();
        };

        // Minimize also goes to tray (configurable in Settings).
        _window.StateChanged += (_, _) =>
        {
            if (_window.WindowState == WindowState.Minimized && _settings.Load().MinimizeToTray)
                HideToTray();
        };
    }

    private void HideToTray()
    {
        _window.Hide();
        if (_balloonShown || !_settings.Load().TrayNotifications) return;
        _balloonShown = true;
        _icon.ShowBalloonTip(3000, "Still running",
            "Schedule Pusher keeps running here in the tray. Right-click the icon to exit.",
            WinForms.ToolTipIcon.Info);
    }

    private void Restore()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
