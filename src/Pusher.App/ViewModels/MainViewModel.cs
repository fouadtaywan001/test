using Pusher.App.Mvvm;
using Pusher.Core.Abstractions;
using Pusher.Storage;

namespace Pusher.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private ObservableObject _current;
    private string _currentSection = "dashboard";

    public DashboardViewModel Dashboard { get; }
    public NewProjectViewModel NewProject { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel Settings { get; }

    public RelayCommand ShowDashboard { get; }
    public RelayCommand ShowNewProject { get; }
    public RelayCommand ShowLogs { get; }
    public RelayCommand ShowSettings { get; }

    public ObservableObject Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    /// <summary>Which sidebar item is active — drives the nav highlight in MainWindow.</summary>
    public string CurrentSection
    {
        get => _currentSection;
        private set
        {
            if (SetProperty(ref _currentSection, value))
            {
                OnPropertyChanged(nameof(IsDashboard));
                OnPropertyChanged(nameof(IsNewProject));
                OnPropertyChanged(nameof(IsLogs));
                OnPropertyChanged(nameof(IsSettings));
            }
        }
    }

    public bool IsDashboard => _currentSection == "dashboard";
    public bool IsNewProject => _currentSection == "new";
    public bool IsLogs => _currentSection == "logs";
    public bool IsSettings => _currentSection == "settings";

    public MainViewModel(IStateStore store, ITokenProtector protector, AppSettingsStore settingsStore)
    {
        var github = new Services.GitHubClientFactory(settingsStore, protector);
        Dashboard = new DashboardViewModel(store, settingsStore);
        NewProject = new NewProjectViewModel(store, settingsStore, github, protector);
        Logs = new LogsViewModel(settingsStore);
        Settings = new SettingsViewModel(protector, settingsStore, github);

        _current = Dashboard;
        ShowDashboard = new RelayCommand(() => { Dashboard.Refresh(); Current = Dashboard; CurrentSection = "dashboard"; });
        ShowNewProject = new RelayCommand(() => { Current = NewProject; CurrentSection = "new"; });
        ShowLogs = new RelayCommand(() => { Logs.Refresh(); Current = Logs; CurrentSection = "logs"; });
        ShowSettings = new RelayCommand(() => { Current = Settings; CurrentSection = "settings"; });
    }
}
