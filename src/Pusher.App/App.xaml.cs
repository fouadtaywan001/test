using System.Windows;
using Pusher.App.Services;
using Pusher.App.ViewModels;
using Pusher.Storage;

namespace Pusher.App;

public partial class App : Application
{
    private TrayIconService? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Composition root — same ProgramData layout the service uses (plan/03 §3).
        var settingsStore = new AppSettingsStore();
        var store = new SqliteStateStore(settingsStore.DatabasePath);
        store.Initialize();
        var protector = new DpapiTokenProtector();

        var window = new MainWindow
        {
            DataContext = new MainViewModel(store, protector, settingsStore),
        };
        _tray = new TrayIconService(window, settingsStore);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
