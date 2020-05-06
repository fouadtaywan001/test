using System.Collections.ObjectModel;
using System.IO;
using Pusher.App.Mvvm;
using Pusher.Core.Abstractions;
using Pusher.Core.Models;
using Pusher.Core.Planning;
using Pusher.Core.Scanning;
using Pusher.Core.Scripting;
using Pusher.Storage;

namespace Pusher.App.ViewModels;

/// <summary>Row in the preview calendar table (wizard step 3).</summary>
public sealed record PreviewRow(int Order, string Date, string Time, string Message, int FileCount, string Mode);

/// <summary>Row in the validation/scanner panel (wizard step 2).</summary>
public sealed record IssueRow(string Code, string Severity, string Location, string Message)
{
    public bool IsError => Severity == "Error";
}

/// <summary>
/// Three-step wizard (plan/06-wpf-ui.md §3): folder+remote -> script -> preview & activate.
/// The remote-state guard and staging snapshot run at step 3 / activation.
/// </summary>
public sealed class NewProjectViewModel : ObservableObject
{
    private readonly IStateStore _store;
    private readonly AppSettingsStore _settingsStore;
    private readonly Services.GitHubClientFactory _github;
    private readonly ITokenProtector _protector;
    private readonly PrePushScanner _scanner = new();

    private int _step = 1;
    private ProjectMode _mode = ProjectMode.Backdated;
    private string _name = "";
    private string _localPath = "";
    private string _remoteUrl = "";
    private bool _createNewRepo = true;
    private bool _newRepoPrivate = true;
    private string _repoStatus = "";
    private bool _busy;
    private string _scriptText = BackdatedTemplate;
    private int _seed = Random.Shared.Next();
    private string _statusMessage = "";
    private PlanResult? _lastPlan;

    public ObservableCollection<IssueRow> Issues { get; } = [];
    public ObservableCollection<PreviewRow> Preview { get; } = [];

    public RelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand CreateRepoCommand { get; }
    public RelayCommand ValidateCommand { get; }
    public RelayCommand CopyIssuesCommand { get; }
    public RelayCommand ShuffleCommand { get; }
    public RelayCommand ActivateCommand { get; }

    public event Action? Activated;

    public int Step { get => _step; private set { if (SetProperty(ref _step, value)) OnStepChanged(); } }

    /// <summary>Backdated = push everything instantly with past dates; RealTime = worker
    /// pushes over time on the planned schedule. Chosen in step 1, drives the template.</summary>
    public ProjectMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsBackdatedMode));
            OnPropertyChanged(nameof(IsRealTimeMode));
            // Templates are mode-specific; swap only while it's still an untouched template.
            if (ScriptText == BackdatedTemplate || ScriptText == RealTimeTemplate || ScriptText.Contains("{{"))
                ScriptText = value == ProjectMode.Backdated ? BackdatedTemplate : RealTimeTemplate;
        }
    }
    public bool IsBackdatedMode { get => Mode == ProjectMode.Backdated; set { if (value) Mode = ProjectMode.Backdated; } }
    public bool IsRealTimeMode { get => Mode == ProjectMode.RealTime; set { if (value) Mode = ProjectMode.RealTime; } }
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(SuggestedRepoName));
        }
    }
    public string LocalPath { get => _localPath; set => SetProperty(ref _localPath, value); }
    public string RemoteUrl { get => _remoteUrl; set => SetProperty(ref _remoteUrl, value); }
    public bool CreateNewRepo
    {
        get => _createNewRepo;
        set
        {
            if (SetProperty(ref _createNewRepo, value)) OnPropertyChanged(nameof(UseExistingRepo));
        }
    }
    public bool UseExistingRepo
    {
        get => !_createNewRepo;
        set => CreateNewRepo = !value;
    }
    public bool NewRepoPrivate { get => _newRepoPrivate; set => SetProperty(ref _newRepoPrivate, value); }
    public string RepoStatus { get => _repoStatus; private set => SetProperty(ref _repoStatus, value); }
    public string SuggestedRepoName => SlugifyRepoName(Name);
    public string ScriptText { get => _scriptText; set => SetProperty(ref _scriptText, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool SeedIsPinned => _lastPlan is not null && PushScriptEngine.Compile(ScriptText).Model?.Options.Seed is not null;

    public NewProjectViewModel(IStateStore store, AppSettingsStore settingsStore,
        Services.GitHubClientFactory github, ITokenProtector protector)
    {
        _store = store;
        _settingsStore = settingsStore;
        _github = github;
        _protector = protector;

        NextCommand = new RelayCommand(GoNext, CanGoNext);
        BackCommand = new RelayCommand(() => Step = Math.Max(1, Step - 1), () => Step > 1);
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        CreateRepoCommand = new RelayCommand(async () => await CreateRepoAsync(), () => !_busy && CreateNewRepo);
        ValidateCommand = new RelayCommand(RunValidation);
        CopyIssuesCommand = new RelayCommand(CopyIssues, () => Issues.Count > 0);
        ShuffleCommand = new RelayCommand(Shuffle, () => IsStep3 && !SeedIsPinned);
        ActivateCommand = new RelayCommand(Activate, () => IsStep3 && _lastPlan is { Success: true });
    }

    private bool CanGoNext() => Step switch
    {
        1 => Name.Trim().Length > 0 && Directory.Exists(LocalPath) && RemoteUrl.Trim().Length > 0,
        2 => Issues.All(i => !i.IsError),
        _ => false,
    };

    /// <summary>Opens the .NET 8 folder picker so the user selects their completed
    /// project directory instead of typing a path (and mistyping it).</summary>
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select your completed project folder",
            InitialDirectory = Directory.Exists(LocalPath) ? LocalPath : "",
        };
        if (dialog.ShowDialog() == true)
        {
            LocalPath = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(Name))
                Name = Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar));
        }
    }

    /// <summary>Creates an empty repo on the user's GitHub account with the saved PAT
    /// and fills in the remote URL. AutoInit is off (see GitHubClientService) so the
    /// target branch stays empty for backdated pushes.</summary>
    private async Task CreateRepoAsync()
    {
        var client = _github.Create();
        if (client is null)
        {
            RepoStatus = "Add your GitHub PAT in Settings first.";
            return;
        }

        var repoName = SuggestedRepoName;
        if (repoName.Length == 0)
        {
            RepoStatus = "Enter a project name first.";
            return;
        }

        _busy = true;
        RepoStatus = $"Creating '{repoName}' on GitHub…";
        try
        {
            var info = await client.CreateRepoAsync(repoName, description: null, isPrivate: NewRepoPrivate);
            RemoteUrl = info.CloneUrl;
            RepoStatus = $"Created {info.Owner}/{info.Name} ({(info.Private ? "private" : "public")}). Remote URL filled in below.";
        }
        catch (Exception ex)
        {
            RepoStatus = $"Could not create repo: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private static string SlugifyRepoName(string name)
    {
        var slug = new string(name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private void GoNext()
    {
        if (Step == 1)
        {
            FillTemplatePlaceholders();
            Step = 2;
            RunValidation();
        }
        else if (Step == 2)
        {
            // Always re-validate with the CURRENT script text: the Issues list may be
            // stale (user edited after validating) or empty (never validated at all).
            RunValidation();
            if (Issues.Any(i => i.IsError)) return;
            Step = 3;
            BuildPreview();
        }
    }

    /// <summary>Substitutes the step-1 remote and the global identity into a fresh template
    /// so the script and wizard never disagree about where pushes go. The split block is
    /// generated from the files actually present in the selected folder, so the default
    /// script always validates cleanly regardless of project layout.</summary>
    private void FillTemplatePlaceholders()
    {
        if (!ScriptText.Contains("{{")) return;
        var s = _settingsStore.Load();
        ScriptText = ScriptText
            .Replace("{{REMOTE}}", RemoteUrl.Trim())
            .Replace("{{NAME}}", string.IsNullOrWhiteSpace(s.AuthorName) ? "Your Name" : s.AuthorName)
            .Replace("{{EMAIL}}", string.IsNullOrWhiteSpace(s.AuthorEmail) ? "you@example.com" : s.AuthorEmail)
            .Replace("{{SPLIT}}", BuildSplitBlock())
            .Replace("{{FROM}}", DateTime.Today.AddMonths(-3).ToString("yyyy-MM-dd"))
            .Replace("{{TO}}", DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"));
    }

    /// <summary>Builds commit groups from an actual folder scan. Each candidate glob is
    /// kept only when it matches at least one not-yet-claimed file (same GlobMatcher the
    /// planner uses), so V-SPLIT-1 "matches no project file" can never fire on defaults.</summary>
    private string BuildSplitBlock()
    {
        var files = Directory.Exists(LocalPath)
            ? ListProjectFiles(LocalPath)
            : [];

        // (message, candidate globs) in commit order; the final catch-all is always added.
        (string Message, string[] Globs)[] candidates =
        [
            ("Initial project setup", ["README*", ".gitignore", "LICENSE*", "*.sln", "*.slnx", "*.csproj", "*.json", "*.yml", "*.yaml", "*.toml", "*.props", "*.targets", "*.config", "*.md", "*.txt"]),
            ("Core implementation", ["src/**", "app/**", "lib/**", "core/**"]),
            ("Tests", ["tests/**", "test/**", "spec/**", "__tests__/**"]),
            ("Documentation", ["docs/**", "doc/**"]),
            ("Assets and resources", ["assets/**", "public/**", "static/**", "resources/**", "images/**"]),
            ("Scripts and tooling", ["scripts/**", "tools/**", "build/**", ".github/**"]),
        ];

        var remaining = new HashSet<string>(files, StringComparer.Ordinal);
        var groups = new List<(string Message, List<string> Globs)>();

        foreach (var (message, globs) in candidates)
        {
            var kept = new List<string>();
            foreach (var glob in globs)
            {
                var hits = remaining.Where(f => Pusher.Core.Splitting.GlobMatcher.IsMatch(f, glob)).ToList();
                if (hits.Count == 0) continue;
                kept.Add(glob);
                foreach (var h in hits) remaining.Remove(h);
            }
            if (kept.Count > 0) groups.Add((message, kept));
        }

        // Catch-all keeps the plan exhaustive; drop it only if nothing is left AND we
        // already have groups (a split with zero commits would not compile).
        if (remaining.Count > 0 || groups.Count == 0)
            groups.Add(("Everything else", ["**"]));

        var sb = new System.Text.StringBuilder();
        sb.Append("split {\n");
        foreach (var (message, globs) in groups)
        {
            sb.Append($"    commit \"{message}\" {{\n");
            sb.Append($"        files = [{string.Join(", ", globs.Select(g => $"\"{g}\""))}]\n");
            sb.Append("    }\n");
        }
        sb.Append('}');
        return sb.ToString();
    }

    private void OnStepChanged()
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
    }

    // ---- step 2: compile + scan --------------------------------------------

    private void RunValidation()
    {
        Issues.Clear();
        var compile = PushScriptEngine.Compile(ScriptText);
        foreach (var d in compile.Diagnostics)
            Issues.Add(new IssueRow(d.Code, d.Severity.ToString(), $"{d.Line}:{d.Column}", d.Message));

        if (compile.Model is null || !Directory.Exists(LocalPath))
        {
            StatusMessage = "Fix script errors to continue.";
            return;
        }

        // Scanner runs over exactly the files the manifest publishes (plan §8.4).
        var files = ListProjectFiles(LocalPath);
        var plan = PushScriptEngine.Plan(ScriptText, files, _seed);
        if (plan.Success)
        {
            var published = plan.Commits.SelectMany(c => c.Files).Distinct().ToList();
            foreach (var f in _scanner.Scan(LocalPath, published))
                Issues.Add(new IssueRow(f.Rule, f.Severity.ToString(), $"{f.File}:{f.Line}", f.Message));
        }

        StatusMessage = Issues.Any(i => i.IsError)
            ? "Errors must be fixed before continuing."
            : $"Script OK — {Issues.Count} finding(s), none blocking.";
    }

    /// <summary>Copies all current findings to the clipboard as tab-separated lines
    /// so they can be pasted into a chat/issue for help fixing them.</summary>
    private void CopyIssues()
    {
        if (Issues.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Code\tSeverity\tWhere\tMessage");
        foreach (var i in Issues)
            sb.AppendLine($"{i.Code}\t{i.Severity}\t{i.Location}\t{i.Message}");
        try
        {
            System.Windows.Clipboard.SetText(sb.ToString());
            StatusMessage = $"{Issues.Count} finding(s) copied to clipboard.";
        }
        catch (Exception)
        {
            // Clipboard can be locked by another process; retry usually succeeds.
            StatusMessage = "Could not access the clipboard — try again.";
        }
    }

    // ---- step 3: preview + activate ----------------------------------------

    private void BuildPreview()
    {
        Preview.Clear();
        var files = ListProjectFiles(LocalPath);
        _lastPlan = PushScriptEngine.Plan(ScriptText, files, _seed);

        foreach (var d in _lastPlan.Diagnostics.Where(d => d.IsError))
            Issues.Add(new IssueRow(d.Code, "Error", $"{d.Line}:{d.Column}", d.Message));

        foreach (var c in _lastPlan.Commits)
            Preview.Add(new PreviewRow(
                c.Order,
                c.ScheduledAt.ToString("yyyy-MM-dd"),
                c.ScheduledAt.ToString("HH:mm"),
                c.Message,
                c.Files.Count,
                c.Mode.ToString()));

        OnPropertyChanged(nameof(SeedIsPinned));
        if (_lastPlan.Success)
        {
            StatusMessage = $"{_lastPlan.Commits.Count} commits planned.";
        }
        else
        {
            var firstError = _lastPlan.Diagnostics.FirstOrDefault(d => d.IsError);
            StatusMessage = firstError is null
                ? "Planning failed — go back and fix the script."
                : $"Planning failed: [{firstError.Code}] {firstError.Message} — go back and fix the script.";
        }
    }

    private void Shuffle()
    {
        _seed = Random.Shared.Next();
        BuildPreview();
    }

    private void Activate()
    {
        if (_lastPlan is not { Success: true } plan) return;

        var compile = PushScriptEngine.Compile(ScriptText);
        if (compile.Model is null) return;

        var project = new Project
        {
            Name = Name.Trim(),
            LocalPath = LocalPath,
            RemoteUrl = RemoteUrl.Trim(),
            Branch = compile.Model.Repo.Branch,
            Status = ProjectStatus.Active,
            Mode = Mode,
            ScriptText = ScriptText,
            ScriptSeed = _seed,
        };
        long id = _store.UpsertProject(project);

        // Staging snapshot (plan/03 §2.3): all commits build from this copy.
        string staging = Path.Combine(_settingsStore.StagingRoot, id.ToString());
        SnapshotTo(staging, plan.Commits.SelectMany(c => c.Files).Distinct());
        project.Id = id;
        project.StagingPath = staging;
        _store.UpsertProject(project);

        var plans = plan.Commits.Select(c => new CommitPlan
        {
            ProjectId = id,
            Order = c.Order,
            Message = c.Message,
            Body = c.Body,
            FileGlobs = c.Globs,
            ResolvedFiles = c.Files,
            Mode = c.Mode,
        }).ToList();
        _store.SaveCommitPlans(id, plans);

        var saved = _store.GetCommitPlans(id);
        var slots = plan.Commits.Zip(saved, (c, cp) => new Slot
        {
            CommitPlanId = cp.Id,
            ScheduledAtUtc = c.ScheduledAt.ToUniversalTime(),
        }).ToList();
        _store.SaveSlots(id, slots);

        var hooks = new List<HookAction>();
        var h = compile.Model.Hooks;
        if (h is not null)
        {
            if (h.AddTopics.Count > 0)
                hooks.Add(new HookAction { ProjectId = id, Kind = "add_topics", PayloadJson = System.Text.Json.JsonSerializer.Serialize(h.AddTopics) });
            if (h.SetWebsite is not null)
                hooks.Add(new HookAction { ProjectId = id, Kind = "set_website", PayloadJson = System.Text.Json.JsonSerializer.Serialize(h.SetWebsite) });
            if (h.MakePublic)
                hooks.Add(new HookAction { ProjectId = id, Kind = "make_public", PayloadJson = "true" });
        }
        if (hooks.Count > 0) _store.SaveHooks(id, hooks);

        if (Mode == ProjectMode.Backdated)
        {
            // Instant batch: everything commits + pushes NOW, dates signed in the past.
            RunInstantBatch(id);
        }
        else
        {
            StatusMessage = "Activated — the background worker will start pushing on schedule.";
            Reset();
            Activated?.Invoke();
        }
    }

    /// <summary>Executes the whole backdated plan immediately on a background thread,
    /// streaming progress into StatusMessage. On failure the project stays Active so
    /// re-activation (or the worker's recovery path) can finish the batch.</summary>
    private async void RunInstantBatch(long projectId)
    {
        var settings = _settingsStore.Load();
        if (settings.ProtectedToken is null)
        {
            StatusMessage = "Add your GitHub PAT in Settings first — the project is saved and will push once a token exists.";
            return;
        }

        _busy = true;
        StatusMessage = "Pushing all commits now…";
        try
        {
            var token = _protector.Unprotect(settings.ProtectedToken);
            var executor = new Pusher.Core.Execution.InstantExecutor(_store, new Pusher.Git.GitEngine());
            var result = await Task.Run(() => executor.Run(projectId,
                settings.AuthorName, settings.AuthorEmail, "x-access-token", token,
                p => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    StatusMessage = $"Committing {p.Done + 1}/{p.Total}: {p.CurrentMessage}")));

            if (result.Success)
            {
                StatusMessage = $"Done — {result.Pushed} commits pushed to GitHub with their backdated timestamps.";
                Reset();
                Activated?.Invoke();
            }
            else
            {
                StatusMessage = $"Batch stopped: {result.Error} The project is saved — fix the issue and it will resume.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Batch failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private void SnapshotTo(string stagingPath, IEnumerable<string> relativeFiles)
    {
        if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
        Directory.CreateDirectory(stagingPath);
        foreach (var rel in relativeFiles)
        {
            var src = Path.Combine(LocalPath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(stagingPath, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
    }

    private void Reset()
    {
        Name = ""; LocalPath = ""; RemoteUrl = "";
        RepoStatus = ""; CreateNewRepo = true; NewRepoPrivate = true;
        _mode = ProjectMode.Backdated;
        OnPropertyChanged(nameof(IsBackdatedMode));
        OnPropertyChanged(nameof(IsRealTimeMode));
        ScriptText = BackdatedTemplate;
        _seed = Random.Shared.Next();
        _lastPlan = null;
        Issues.Clear(); Preview.Clear();
        Step = 1;
    }

    internal static List<string> ListProjectFiles(string root)
    {
        var list = new List<string>();
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            // Skip build outputs / VCS folders anywhere in the path (bin, obj, .git, ...):
            // the same list the scanner warns about, applied up front so those files are
            // never planned, scanned, staged, or pushed.
            var segments = rel.Split('/');
            if (segments.Take(segments.Length - 1)
                .Any(s => PrePushScanner.IgnoredFolderNames.Contains(s, StringComparer.OrdinalIgnoreCase)))
                continue;
            list.Add(rel);
        }
        return list;
    }

    /// <summary>Backdated: pure-backfill script — a 3-month window ending yesterday,
    /// commits = all, no schedule block. Executes instantly at activation.
    /// {{FROM}}/{{TO}} are substituted in FillTemplatePlaceholders.</summary>
    private const string BackdatedTemplate = """
        pushscript 1.0

        repo {
            remote = "{{REMOTE}}"
            branch = "main"
            identity = { name = "{{NAME}}", email = "{{EMAIL}}" }
            visibility = private
        }

        {{SPLIT}}

        backdate {
            from = {{FROM}}
            to = {{TO}}
            per_day = random(1, 3)
            active_hours = 09:30 .. 22:00
            rest_days = 20%
            commits = all
        }
        """;

    /// <summary>RealTime: forward schedule — the worker pushes over the coming days.</summary>
    private const string RealTimeTemplate = """
        pushscript 1.0

        repo {
            remote = "{{REMOTE}}"
            branch = "main"
            identity = { name = "{{NAME}}", email = "{{EMAIL}}" }
            visibility = private
        }

        {{SPLIT}}

        schedule {
            start = today
            per_day = random(1, 2)
            active_hours = 18:00 .. 22:30
        }
        """;
}
