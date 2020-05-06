using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Pusher.App.Mvvm;
using Pusher.Storage;

namespace Pusher.App.ViewModels;

/// <summary>One parsed line of a Serilog file, colored by level.</summary>
public sealed class LogLine
{
    public required string Text { get; init; }
    public required string Level { get; init; }   // INF / WRN / ERR / FTL / DBG / ""

    public string LevelColor => Level switch
    {
        "ERR" or "FTL" => "#F85149",
        "WRN" => "#D29922",
        "INF" => "#2EA043",
        _ => "#8B949E",
    };
}

/// <summary>
/// Log viewer for the background worker's rolling Serilog files
/// (ProgramData\GitHubSchedulePusher\logs\service-*.log). Read-only tail
/// with level + text filters; shares no file handles with the service
/// (opens with ReadWrite share so the worker can keep writing).
/// </summary>
public sealed class LogsViewModel : ObservableObject
{
    private const int MaxLines = 800;

    private readonly string _logsDir;
    private string? _selectedFile;
    private string _filterText = "";
    private string _levelFilter = "All";
    private string _statusMessage = "";
    private List<LogLine> _allLines = [];

    public ObservableCollection<string> LogFiles { get; } = [];
    public ObservableCollection<LogLine> Lines { get; } = [];
    public string[] LevelFilters { get; } = ["All", "INF", "WRN", "ERR"];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public string? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value)) LoadSelectedFile();
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value)) ApplyFilter();
        }
    }

    public string LevelFilter
    {
        get => _levelFilter;
        set
        {
            if (SetProperty(ref _levelFilter, value)) ApplyFilter();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasFiles => LogFiles.Count > 0;

    public LogsViewModel(AppSettingsStore settingsStore)
    {
        _logsDir = Path.Combine(settingsStore.BaseDirectory, "logs");
        RefreshCommand = new RelayCommand(Refresh);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        Refresh();
    }

    public void Refresh()
    {
        var keep = SelectedFile;
        LogFiles.Clear();

        if (Directory.Exists(_logsDir))
        {
            foreach (var f in Directory.EnumerateFiles(_logsDir, "service-*.log")
                         .OrderByDescending(f => f, StringComparer.Ordinal))
                LogFiles.Add(Path.GetFileName(f));
        }

        OnPropertyChanged(nameof(HasFiles));

        if (LogFiles.Count == 0)
        {
            _allLines = [];
            Lines.Clear();
            StatusMessage = "No log files yet — the worker writes logs once it is installed and running (see Settings).";
            return;
        }

        // Keep the current file selected across refreshes; default to the newest.
        var target = keep is not null && LogFiles.Contains(keep) ? keep : LogFiles[0];
        if (target == SelectedFile) LoadSelectedFile();
        else SelectedFile = target;
    }

    private void LoadSelectedFile()
    {
        _allLines = [];
        Lines.Clear();
        if (SelectedFile is null) return;

        var path = Path.Combine(_logsDir, SelectedFile);
        try
        {
            // ReadWrite share: never lock the file the worker is appending to.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var raw = new List<string>();
            while (reader.ReadLine() is { } line) raw.Add(line);

            _allLines = raw
                .Skip(Math.Max(0, raw.Count - MaxLines))
                .Select(l => new LogLine { Text = l, Level = ParseLevel(l) })
                .ToList();

            StatusMessage = raw.Count > MaxLines
                ? $"Showing last {MaxLines} of {raw.Count} lines."
                : $"{raw.Count} lines.";
        }
        catch (IOException ex)
        {
            StatusMessage = $"Could not read log file: {ex.Message}";
            return;
        }
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "Access denied — run the app as the same user the service logs under, or check folder permissions.";
            return;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Lines.Clear();
        foreach (var line in _allLines)
        {
            if (LevelFilter != "All" && line.Level != LevelFilter) continue;
            if (FilterText.Length > 0 && !line.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase)) continue;
            Lines.Add(line);
        }
    }

    /// <summary>Serilog default template: "2026-08-04 10:00:00.000 +00:00 [INF] message".</summary>
    private static string ParseLevel(string line)
    {
        var open = line.IndexOf('[');
        if (open < 0 || open + 4 > line.Length) return "";
        var close = line.IndexOf(']', open);
        return close == open + 4 ? line.Substring(open + 1, 3) : "";
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(_logsDir);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _logsDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open folder: {ex.Message}";
        }
    }
}
