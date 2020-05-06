using System.Collections.ObjectModel;
using Pusher.App.Mvvm;
using Pusher.App.Services;
using Pusher.Core.Models;
using Pusher.Storage;

namespace Pusher.App.ViewModels;

/// <summary>Global settings: PAT (DPAPI-protected at rest), author identity
/// (auto-fetchable from GitHub), catch-up policy, and one-click worker install.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly Pusher.Core.Abstractions.ITokenProtector _protector;
    private readonly AppSettingsStore _store;
    private readonly GitHubClientFactory _github;
    private readonly WorkerServiceManager _worker = new();

    private string _token = "";
    private string _authorName = "";
    private string _authorEmail = "";
    private bool _catchUpImmediate = true;
    private string _statusMessage = "";
    private string _identityStatus = "";
    private string _workerStatus = "";
    private bool _busy;

    // application behavior
    private bool _closeToTray = true;
    private bool _minimizeToTray = true;
    private bool _trayNotifications = true;
    private bool _startWithWindows;
    private bool _confirmBeforeDelete = true;

    // scheduling defaults
    private int _catchUpMaxPerDay = 3;
    private int _pollIntervalSeconds = 60;

    public ObservableCollection<string> VerifiedEmails { get; } = [];

    public RelayCommand SaveCommand { get; }
    public RelayCommand FetchIdentityCommand { get; }
    public RelayCommand InstallWorkerCommand { get; }
    public RelayCommand UninstallWorkerCommand { get; }
    public RelayCommand StartWorkerCommand { get; }
    public RelayCommand StopWorkerCommand { get; }
    public RelayCommand RefreshWorkerCommand { get; }

    public SettingsViewModel(Pusher.Core.Abstractions.ITokenProtector protector, AppSettingsStore store, GitHubClientFactory github)
    {
        _protector = protector;
        _store = store;
        _github = github;
        SaveCommand = new RelayCommand(Save);
        FetchIdentityCommand = new RelayCommand(async () => await FetchIdentityAsync(), () => !_busy);
        InstallWorkerCommand = new RelayCommand(InstallWorker, () => !_busy);
        UninstallWorkerCommand = new RelayCommand(UninstallWorker, () => !_busy);
        StartWorkerCommand = new RelayCommand(StartWorker, () => !_busy);
        StopWorkerCommand = new RelayCommand(StopWorker, () => !_busy);
        RefreshWorkerCommand = new RelayCommand(RefreshWorkerStatus);

        var s = _store.Load();
        _authorName = s.AuthorName;
        _authorEmail = s.AuthorEmail;
        _catchUpImmediate = s.CatchUp == CatchUpPolicy.Immediate;
        _closeToTray = s.CloseToTray;
        _minimizeToTray = s.MinimizeToTray;
        _trayNotifications = s.TrayNotifications;
        _startWithWindows = StartupManager.IsEnabled(); // registry is the source of truth
        _confirmBeforeDelete = s.ConfirmBeforeDelete;
        _catchUpMaxPerDay = s.CatchUpMaxPerDay;
        _pollIntervalSeconds = s.PollIntervalSeconds;
        if (!string.IsNullOrWhiteSpace(_authorEmail)) VerifiedEmails.Add(_authorEmail);
        // The token is never round-tripped into the UI; leave the box blank unless replacing.
        RefreshWorkerStatus();
    }

    public string Token { get => _token; set => SetProperty(ref _token, value); }
    public string AuthorName { get => _authorName; set => SetProperty(ref _authorName, value); }
    public string AuthorEmail { get => _authorEmail; set => SetProperty(ref _authorEmail, value); }
    public bool CatchUpImmediate { get => _catchUpImmediate; set => SetProperty(ref _catchUpImmediate, value); }

    public bool CloseToTray { get => _closeToTray; set => SetProperty(ref _closeToTray, value); }
    public bool MinimizeToTray { get => _minimizeToTray; set => SetProperty(ref _minimizeToTray, value); }
    public bool TrayNotifications { get => _trayNotifications; set => SetProperty(ref _trayNotifications, value); }
    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }
    public bool ConfirmBeforeDelete { get => _confirmBeforeDelete; set => SetProperty(ref _confirmBeforeDelete, value); }

    /// <summary>UI options for catch-up cap and worker poll cadence
    /// (instance properties: WPF Binding paths cannot resolve statics).</summary>
    public int[] CatchUpMaxOptions { get; } = [1, 2, 3, 5, 10];
    public int[] PollIntervalOptions { get; } = [15, 30, 60, 120, 300];
    public int CatchUpMaxPerDay { get => _catchUpMaxPerDay; set => SetProperty(ref _catchUpMaxPerDay, value); }
    public int PollIntervalSeconds { get => _pollIntervalSeconds; set => SetProperty(ref _pollIntervalSeconds, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string IdentityStatus { get => _identityStatus; private set => SetProperty(ref _identityStatus, value); }
    public string WorkerStatus { get => _workerStatus; private set => SetProperty(ref _workerStatus, value); }

    public bool HasToken => !string.IsNullOrEmpty(_store.Load().ProtectedToken);

    private void Save()
    {
        var s = _store.Load();
        s.AuthorName = AuthorName.Trim();
        s.AuthorEmail = AuthorEmail.Trim();
        s.CatchUp = CatchUpImmediate ? CatchUpPolicy.Immediate : CatchUpPolicy.Reschedule;
        s.CloseToTray = CloseToTray;
        s.MinimizeToTray = MinimizeToTray;
        s.TrayNotifications = TrayNotifications;
        s.StartWithWindows = StartWithWindows;
        s.ConfirmBeforeDelete = ConfirmBeforeDelete;
        s.CatchUpMaxPerDay = CatchUpMaxPerDay;
        s.PollIntervalSeconds = PollIntervalSeconds;

        if (!string.IsNullOrWhiteSpace(Token))
        {
            s.ProtectedToken = _protector.Protect(Token.Trim());
            Token = "";
        }

        _store.Save(s);

        // Apply the HKCU Run key to match the checkbox; report failure but keep other settings.
        var startupError = StartupManager.SetEnabled(StartWithWindows);

        OnPropertyChanged(nameof(HasToken));
        StatusMessage = startupError ?? "Settings saved.";
    }

    /// <summary>Pulls login/display-name + verified emails from GitHub using the
    /// typed-or-saved PAT, so the user never types their identity by hand.</summary>
    private async Task FetchIdentityAsync()
    {
        var client = _github.Create(Token); // prefer a freshly typed token
        if (client is null)
        {
            IdentityStatus = "Enter your PAT above (or Save it) first.";
            return;
        }

        _busy = true;
        IdentityStatus = "Fetching from GitHub…";
        try
        {
            var id = await client.GetIdentityAsync();
            AuthorName = string.IsNullOrWhiteSpace(id.Name) ? id.Login : id.Name!;

            VerifiedEmails.Clear();
            foreach (var email in id.VerifiedEmails) VerifiedEmails.Add(email);

            if (id.VerifiedEmails.Count > 0) AuthorEmail = id.VerifiedEmails[0];
            IdentityStatus = id.VerifiedEmails.Count > 0
                ? $"Signed in as {id.Login} — {id.VerifiedEmails.Count} verified email(s)."
                : $"Signed in as {id.Login}, but no verified email was found. Verify one on GitHub.";
        }
        catch (Exception ex)
        {
            IdentityStatus = $"Could not reach GitHub: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    // ---- worker (Windows Service) ------------------------------------------

    private void InstallWorker()
    {
        var exe = WorkerServiceManager.FindServiceExe();
        if (exe is null)
        {
            WorkerStatus = "Could not locate Pusher.Service.exe. Rebuild the solution (the app now copies the worker into a 'service' folder next to it) and try again.";
            return;
        }
        var (_, message) = _worker.Install(exe);
        WorkerStatus = message;
        RefreshWorkerStatus();
    }

    private void UninstallWorker()
    {
        WorkerStatus = _worker.Uninstall().Message;
        RefreshWorkerStatus();
    }

    private void StartWorker()
    {
        WorkerStatus = _worker.Start().Message;
        RefreshWorkerStatus();
    }

    private void StopWorker()
    {
        WorkerStatus = _worker.Stop().Message;
        RefreshWorkerStatus();
    }

    private void RefreshWorkerStatus()
    {
        bool installed = _worker.IsInstalled();
        WorkerStatus = installed
            ? (_worker.IsRunning() ? "Installed · Running" : "Installed · Stopped")
            : "Not installed";
        OnPropertyChanged(nameof(WorkerInstalled));
        OnPropertyChanged(nameof(WorkerNotInstalled));
    }

    public bool WorkerInstalled => _worker.IsInstalled();
    public bool WorkerNotInstalled => !WorkerInstalled;
}
