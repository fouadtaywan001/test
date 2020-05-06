using System.Collections.ObjectModel;
using System.IO;
using Pusher.App.Mvvm;
using Pusher.Core.Abstractions;
using Pusher.Core.Models;

namespace Pusher.App.ViewModels;

/// <summary>One planned commit inside an expanded project card. Pending rows are
/// editable inline: message and planned date/time can change before the push runs.</summary>
public sealed class SlotDetailRow : ObservableObject
{
    public required long PlanId { get; init; }
    public required int Order { get; init; }
    public required DateTimeOffset? ExecutedUtc { get; init; }
    public required SlotResult Result { get; init; }
    public required string? Sha { get; init; }
    public required string Log { get; init; }
    public required IStateStore Store { get; init; }
    /// <summary>Owner callback: refreshes card aggregates (next push, stats) after a save.</summary>
    public required Action OnEdited { get; init; }

    private string _message = "";
    private DateTimeOffset? _plannedUtc;
    private bool _isEditing;
    private string _editMessage = "";
    private string _editWhen = "";
    private string _editError = "";

    public required string Message { get => _message; init => _message = value; }
    public required DateTimeOffset? PlannedUtc { get => _plannedUtc; init => _plannedUtc = value; }

    /// <summary>Only commits that have not run yet can be edited.</summary>
    public bool CanEdit => Result == SlotResult.None && ExecutedUtc is null && PlannedUtc is not null;

    public bool IsEditing { get => _isEditing; private set => SetProperty(ref _isEditing, value); }
    public string EditMessage { get => _editMessage; set => SetProperty(ref _editMessage, value); }
    /// <summary>Editable local timestamp, "yyyy-MM-dd HH:mm".</summary>
    public string EditWhen { get => _editWhen; set => SetProperty(ref _editWhen, value); }
    public string EditError { get => _editError; private set => SetProperty(ref _editError, value); }

    public RelayCommand BeginEditCommand => new(() =>
    {
        if (!CanEdit) return;
        EditMessage = Message;
        EditWhen = PlannedUtc!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        EditError = "";
        IsEditing = true;
    });

    public RelayCommand CancelEditCommand => new(() => IsEditing = false);

    public RelayCommand SaveEditCommand => new(() =>
    {
        var msg = EditMessage.Trim();
        if (msg.Length == 0) { EditError = "Message cannot be empty."; return; }
        if (!DateTimeOffset.TryParseExact(EditWhen.Trim(), "yyyy-MM-dd HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var when))
        {
            EditError = "Use the format yyyy-MM-dd HH:mm (e.g. 2026-08-04 14:30).";
            return;
        }

        Store.UpdatePendingCommitMessage(PlanId, msg);
        Store.UpdatePendingSlotSchedule(PlanId, when.ToUniversalTime());

        _message = msg;
        _plannedUtc = when.ToUniversalTime();
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Planned));
        IsEditing = false;
        OnEdited();
    });

    /// <summary>Tooltip: the failure reason for failed slots, empty otherwise.</summary>
    public string? StatusTooltip => Result == SlotResult.Failed && Log.Length > 0 ? Log : null;

    public string OrderLabel => Order.ToString("00");
    public string Planned => PlannedUtc?.ToLocalTime().ToString("d MMM HH:mm") ?? "—";
    public string Actual => ExecutedUtc?.ToLocalTime().ToString("HH:mm") ?? "—";
    public string ShortSha => string.IsNullOrEmpty(Sha) ? "—" : Sha[..Math.Min(8, Sha.Length)];

    public string StatusLabel => Result switch
    {
        SlotResult.Success => "Pushed",
        SlotResult.Failed => "Failed",
        SlotResult.Skipped => "Skipped",
        _ => ExecutedUtc is null ? "Pending" : "Done",
    };

    public string StatusColor => Result switch
    {
        SlotResult.Success => "#2EA043",
        SlotResult.Failed => "#F85149",
        SlotResult.Skipped => "#D29922",
        _ => "#8B949E",
    };
}

/// <summary>One row on the dashboard: project + aggregate slot progress. Expands to show
/// the full commit-by-commit schedule, loaded lazily on first expand.</summary>
public sealed class ProjectRow : ObservableObject
{
    public required Project Project { get; init; }
    public required int TotalSlots { get; init; }
    public required int PushedSlots { get; init; }
    public required int FailedSlots { get; init; }
    public required DateTimeOffset? NextSlotLocal { get; init; }
    public required IStateStore Store { get; init; }

    private bool _isExpanded;
    private bool _detailsLoaded;

    /// <summary>Set by the dashboard: called after an inline slot edit so aggregates
    /// (next push, stat cards) refresh without collapsing the card.</summary>
    public Action? OnDetailEdited { get; set; }

    public ObservableCollection<SlotDetailRow> Details { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value && !_detailsLoaded) LoadDetails();
        }
    }

    private void LoadDetails()
    {
        _detailsLoaded = true;
        Details.Clear();
        var slots = Store.GetSlots(Project.Id).ToDictionary(s => s.CommitPlanId);
        foreach (var plan in Store.GetCommitPlans(Project.Id).OrderBy(c => c.Order))
        {
            slots.TryGetValue(plan.Id, out var slot);
            Details.Add(new SlotDetailRow
            {
                PlanId = plan.Id,
                Order = plan.Order,
                Message = plan.Message,
                PlannedUtc = slot?.ScheduledAtUtc,
                ExecutedUtc = slot?.ExecutedAtUtc,
                Result = slot?.Result ?? SlotResult.None,
                Sha = plan.CommitSha,
                Log = slot?.Log ?? "",
                Store = Store,
                OnEdited = () => OnDetailEdited?.Invoke(),
            });
        }
    }

    public string Name => Project.Name;
    public string Status => Project.Status.ToString();
    public string ModeLabel => Project.Mode == ProjectMode.Backdated ? "Backdated" : "Real-time";
    public string ModeColor => Project.Mode == ProjectMode.Backdated ? "#D29922" : "#58A6FF";
    public string NextPushLabel => Project.Mode == ProjectMode.Backdated ? "pushed instantly" : NextPush;
    public string Progress => TotalSlots == 0 ? "—" : $"{PushedSlots}/{TotalSlots}";
    public double ProgressPercent => TotalSlots == 0 ? 0 : PushedSlots * 100.0 / TotalSlots;
    public string NextPush => NextSlotLocal is null ? "—" : NextSlotLocal.Value.ToLocalTime().ToString("ddd d MMM HH:mm");
    public string BranchLabel => Project.Branch;

    /// <summary>Hex brush for the status chip — WPF's default BrushConverter handles the string.</summary>
    public string StatusColor => Project.Status switch
    {
        ProjectStatus.Active => "#2EA043",
        ProjectStatus.Paused => "#D29922",
        ProjectStatus.Completed => "#58A6FF",
        ProjectStatus.Attention => "#F85149",
        _ => "#8B949E",
    };
}

/// <summary>One entry in the recent-activity feed (an executed slot).</summary>
public sealed class ActivityRow
{
    public required string ProjectName { get; init; }
    public required SlotResult Result { get; init; }
    public required DateTimeOffset ExecutedAtUtc { get; init; }
    public required string Log { get; init; }

    public string When => ExecutedAtUtc.ToLocalTime().ToString("d MMM HH:mm");
    public string ResultLabel => Result.ToString();
    public string ResultColor => Result switch
    {
        SlotResult.Success => "#2EA043",
        SlotResult.Failed => "#F85149",
        SlotResult.Skipped => "#D29922",
        _ => "#8B949E",
    };
    public string Summary
    {
        get
        {
            var firstLine = Log.Split('\n').FirstOrDefault()?.Trim() ?? "";
            return firstLine.Length > 90 ? firstLine[..90] + "…" : firstLine;
        }
    }
}

public sealed class DashboardViewModel : ObservableObject
{
    private readonly IStateStore _store;
    private readonly Pusher.Storage.AppSettingsStore? _settings;
    private ProjectRow? _selected;

    public ObservableCollection<ProjectRow> Projects { get; } = [];
    public ObservableCollection<ActivityRow> RecentActivity { get; } = [];
    public RelayCommand RefreshCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public RelayCommand RetryFailedCommand { get; }
    public RelayCommand EditScriptCommand { get; }
    public RelayCommand RescheduleCommand { get; }
    public RelayCommand PushNowCommand { get; }
    public RelayCommand ToggleExpandCommand { get; }

    public ProjectRow? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public bool HasProjects => Projects.Count > 0;
    public bool HasActivity => RecentActivity.Count > 0;

    // ---- Live worker status (pinged over the named pipe on every refresh) ----
    private bool _workerDown;
    public bool WorkerDown { get => _workerDown; private set => SetProperty(ref _workerDown, value); }

    private bool _workerOutdated;
    /// <summary>True when a worker answers but with a different protocol version:
    /// the installed service binary is a stale build and must be reinstalled.</summary>
    public bool WorkerOutdated { get => _workerOutdated; private set => SetProperty(ref _workerOutdated, value); }

    /// <summary>Pings the worker; flips the warning banners. Called on refresh and after retry.</summary>
    private async void CheckWorkerAsync()
    {
        var response = await Pusher.Ipc.PipeClient.SendAsync(new Pusher.Ipc.PipeRequest("ping"));
        WorkerDown = !response.Ok;
        WorkerOutdated = response.Ok && response.Message != $"pong:{Pusher.Ipc.PipeProtocol.Version}";
    }

    /// <summary>Fire-and-forget "wake" so due slots run now instead of at the next poll.</summary>
    private static async void WakeWorkerAsync()
    {
        try { await Pusher.Ipc.PipeClient.SendAsync(new Pusher.Ipc.PipeRequest("wake")); }
        catch { /* worker not running — the banner already tells the user */ }
    }

    // ---- Stat cards ----
    public int TotalProjects => Projects.Count;
    public int ActiveProjects => Projects.Count(p => p.Project.Status == ProjectStatus.Active);
    public int TotalPushed => Projects.Sum(p => p.PushedSlots);
    public int TotalPending => Projects.Sum(p => p.TotalSlots - p.PushedSlots - p.FailedSlots);
    public string NextPushOverall
    {
        get
        {
            var next = Projects
                .Where(p => p.Project.Status == ProjectStatus.Active && p.NextSlotLocal is not null)
                .Select(p => p.NextSlotLocal!.Value)
                .OrderBy(t => t)
                .Cast<DateTimeOffset?>()
                .FirstOrDefault();
            return next is null ? "—" : next.Value.ToLocalTime().ToString("ddd d MMM HH:mm");
        }
    }

    public DashboardViewModel(IStateStore store, Pusher.Storage.AppSettingsStore? settings = null)
    {
        _store = store;
        _settings = settings;
        RefreshCommand = new RelayCommand(Refresh);
        DeleteCommand = new RelayCommand(DeleteSelected, () => Selected is not null);
        TogglePauseCommand = new RelayCommand(TogglePause, () => Selected is not null);
        ExportReportCommand = new RelayCommand(ExportReport, () => Selected is not null);
        RetryFailedCommand = new RelayCommand(RetryFailed, () => Selected is { FailedSlots: > 0 });
        EditScriptCommand = new RelayCommand(EditScript, () => Selected is not null);
        RescheduleCommand = new RelayCommand(OpenReschedule);
        PushNowCommand = new RelayCommand(PushNow, () => Selected is not null);
        ToggleExpandCommand = new RelayCommand(p =>
        {
            if (p is not ProjectRow row) return;
            row.IsExpanded = !row.IsExpanded;
            Selected = row; // expanding also selects, so header actions target this project
        });
        Refresh();
        PromptForMissedOnStartup();
    }

    /// <summary>Runs the selected project's HEAD commit (lowest pending order) immediately:
    /// its slot is set to now and the worker is woken. Order stays intact — this can only
    /// ever accelerate the next commit, never jump ahead in the sequence.</summary>
    private void PushNow()
    {
        if (Selected is null) return;
        var projectId = Selected.Project.Id;
        var slots = _store.GetSlots(projectId).ToDictionary(s => s.CommitPlanId);
        var headPlan = _store.GetCommitPlans(projectId)
            .Where(p => p.Status == CommitPlanStatus.Pending && slots.ContainsKey(p.Id))
            .OrderBy(p => p.Order)
            .FirstOrDefault();
        if (headPlan is null) return;

        _store.UpdatePendingSlotSchedule(headPlan.Id, DateTimeOffset.UtcNow);
        WakeWorkerAsync();
        Refresh();
    }

    // ---- Missed-push reschedule popup ----

    private static bool _missedPromptShown; // once per app session

    /// <summary>Pops the reschedule dialog after the main window is up when any active
    /// project's next pending commit was missed. Deferred via the dispatcher so it never
    /// races window creation.</summary>
    private void PromptForMissedOnStartup()
    {
        if (_missedPromptShown || !RescheduleViewModel.AnyMissed(_store)) return;
        _missedPromptShown = true;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            OpenReschedule);
    }

    /// <summary>Opens the reschedule dialog (also on demand from the toolbar).</summary>
    private void OpenReschedule()
    {
        var window = new Views.RescheduleWindow(new RescheduleViewModel(_store))
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (window.ShowDialog() == true)
        {
            WakeWorkerAsync(); // catch-up head slot is due immediately
        }
        Refresh();
    }

    public void Refresh()
    {
        CheckWorkerAsync();
        // Remember expanded cards (and selection) so refresh doesn't collapse them.
        var expandedIds = Projects.Where(r => r.IsExpanded).Select(r => r.Project.Id).ToHashSet();
        var selectedId = Selected?.Project.Id;
        Projects.Clear();
        RecentActivity.Clear();
        var executed = new List<(string ProjectName, Slot Slot)>();

        foreach (var p in _store.GetAllProjects())
        {
            var slots = _store.GetSlots(p.Id);
            var pushed = slots.Count(s => s.Result == SlotResult.Success);
            var failed = slots.Count(s => s.Result == SlotResult.Failed);
            var next = slots
                .Where(s => s.Result == SlotResult.None)
                .OrderBy(s => s.ScheduledAtUtc)
                .Select(s => (DateTimeOffset?)s.ScheduledAtUtc)
                .FirstOrDefault();

            var row = new ProjectRow
            {
                Project = p,
                TotalSlots = slots.Count,
                PushedSlots = pushed,
                FailedSlots = failed,
                NextSlotLocal = next,
                Store = _store,
            };
            row.OnDetailEdited = Refresh;
            Projects.Add(row);

            if (expandedIds.Contains(p.Id)) row.IsExpanded = true;
            if (selectedId == p.Id) Selected = row;

            executed.AddRange(slots
                .Where(s => s.ExecutedAtUtc is not null)
                .Select(s => (p.Name, s)));
        }

        foreach (var (name, slot) in executed
            .OrderByDescending(e => e.Slot.ExecutedAtUtc)
            .Take(12))
        {
            RecentActivity.Add(new ActivityRow
            {
                ProjectName = name,
                Result = slot.Result,
                ExecutedAtUtc = slot.ExecutedAtUtc!.Value,
                Log = slot.Log,
            });
        }

        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(TotalProjects));
        OnPropertyChanged(nameof(ActiveProjects));
        OnPropertyChanged(nameof(TotalPushed));
        OnPropertyChanged(nameof(TotalPending));
        OnPropertyChanged(nameof(NextPushOverall));
    }

    private void DeleteSelected()
    {
        if (Selected is null) return;

        // Optional confirmation (Settings > Application > "Ask for confirmation before deleting").
        if (_settings?.Load().ConfirmBeforeDelete ?? true)
        {
            var answer = System.Windows.MessageBox.Show(
                $"Delete project \"{Selected.Name}\" and its staging clone?\n\nYour live folder and the GitHub repo are never touched.",
                "Delete project", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;
        }

        var staging = Selected.Project.StagingPath;

        _store.DeleteProject(Selected.Project.Id);

        // Remove the staging clone; never the live folder or the GitHub repo (plan/06 §4).
        if (!string.IsNullOrEmpty(staging) && Directory.Exists(staging))
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { /* best effort; worker cleanup sweep will retry */ }
            catch (UnauthorizedAccessException) { }
        }
        Refresh();
    }

    private void TogglePause()
    {
        if (Selected is null) return;
        var p = Selected.Project;
        p.Status = p.Status == ProjectStatus.Paused ? ProjectStatus.Active : ProjectStatus.Paused;
        _store.UpsertProject(p);
        Refresh();
    }

    /// <summary>Opens the stored PushScript in a modal editor. On save the pending
    /// schedule is re-planned and the dashboard refreshed; executed pushes stay intact.</summary>
    private void EditScript()
    {
        if (Selected is null) return;
        var vm = new EditScriptViewModel(_store, Selected.Project);
        var window = new Views.EditScriptWindow(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (window.ShowDialog() == true)
        {
            WakeWorkerAsync(); // any newly due slot runs now rather than at the next poll
            Refresh();
        }
    }

    /// <summary>Requeues all Failed commits of the selected project. Their slots are already
    /// past due, so the worker retries them on its next poll cycle (within ~1 minute).</summary>
    private void RetryFailed()
    {
        if (Selected is null) return;
        _store.ResetFailed(Selected.Project.Id);
        WakeWorkerAsync(); // execute the requeued slots now, not at the next poll
        Refresh();
    }

    /// <summary>On-time tolerance: a push within this window of its scheduled time counts as on time
    /// (the worker polls every 60s, so up to ~1 min of drift is expected by design).</summary>
    private static readonly TimeSpan OnTimeTolerance = TimeSpan.FromMinutes(2);

    /// <summary>Writes a planned-vs-actual markdown report for the selected project to the
    /// Desktop and opens it. This is the post-test verification artifact.</summary>
    private void ExportReport()
    {
        if (Selected is null) return;
        var project = Selected.Project;
        var plans = _store.GetCommitPlans(project.Id).OrderBy(c => c.Order).ToList();
        var slots = _store.GetSlots(project.Id);
        var slotByPlan = slots.ToDictionary(s => s.CommitPlanId);

        var pushed = 0; var failed = 0; var pending = 0; var onTime = 0;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Push report — {project.Name}");
        sb.AppendLine();
        sb.AppendLine($"- Generated: {DateTimeOffset.Now:ddd d MMM yyyy HH:mm:ss (zzz)}");
        sb.AppendLine($"- Remote: {project.RemoteUrl} (branch `{project.Branch}`)");
        sb.AppendLine($"- Status: {project.Status} | Seed: {project.ScriptSeed}");
        sb.AppendLine($"- On-time tolerance: {OnTimeTolerance.TotalMinutes:0} min (worker polls every 60 s)");
        sb.AppendLine();
        sb.AppendLine("| # | Commit | Planned (local) | Actual (local) | Delta | Result | SHA |");
        sb.AppendLine("|---|--------|-----------------|----------------|-------|--------|-----|");

        foreach (var plan in plans)
        {
            slotByPlan.TryGetValue(plan.Id, out var slot);
            var planned = slot?.ScheduledAtUtc.ToLocalTime();
            var actual = slot?.ExecutedAtUtc?.ToLocalTime();
            string delta = "—", result, sha = plan.CommitSha is null ? "—" : plan.CommitSha[..Math.Min(8, plan.CommitSha.Length)];

            if (actual is not null && planned is not null)
            {
                var d = actual.Value - planned.Value;
                delta = $"{(d < TimeSpan.Zero ? "-" : "+")}{Math.Abs(d.TotalMinutes):0.0} min";
                var isOnTime = d.Duration() <= OnTimeTolerance;
                if (slot!.Result == SlotResult.Success)
                {
                    pushed++;
                    if (isOnTime) onTime++;
                    result = isOnTime ? "PUSHED (on time)" : "PUSHED (late)";
                }
                else if (slot.Result == SlotResult.Failed) { failed++; result = "FAILED"; }
                else { result = slot.Result.ToString().ToUpperInvariant(); }
            }
            else
            {
                pending++;
                result = "PENDING";
            }

            sb.AppendLine($"| {plan.Order} | {plan.Message} | {planned?.ToString("HH:mm:ss") ?? "—"} | {actual?.ToString("HH:mm:ss") ?? "—"} | {delta} | {result} | {sha} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- Total commits: {plans.Count}");
        sb.AppendLine($"- Pushed: {pushed} ({onTime} on time, {pushed - onTime} late)");
        sb.AppendLine($"- Failed: {failed}");
        sb.AppendLine($"- Pending: {pending}");
        sb.AppendLine();
        var verdict = failed == 0 && pending == 0 && onTime == plans.Count
            ? "ALL COMMITS PUSHED ON SCHEDULE — test passed."
            : failed == 0 && pending == 0
                ? "All commits pushed, but some were outside the on-time tolerance — check worker uptime and the Logs page."
                : pending > 0
                    ? "Test still in progress — some slots have not executed yet. Re-export after the window ends."
                    : "Some pushes FAILED — open the Logs page for the worker error details.";
        sb.AppendLine($"**Verdict:** {verdict}");

        // Slot logs make the report self-contained for failure analysis.
        var failedLogs = slots.Where(s => s.Result == SlotResult.Failed && s.Log.Length > 0).ToList();
        if (failedLogs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Failure logs");
            foreach (var s in failedLogs)
            {
                sb.AppendLine();
                sb.AppendLine($"### Slot {s.Id} ({s.ScheduledAtUtc.ToLocalTime():HH:mm:ss})");
                sb.AppendLine("```");
                sb.AppendLine(s.Log.Trim());
                sb.AppendLine("```");
            }
        }

        var safeName = string.Concat(project.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"push-report-{safeName}-{DateTime.Now:yyyyMMdd-HHmm}.md");
        File.WriteAllText(path, sb.ToString());

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception) { /* report is on the Desktop even if no .md handler is registered */ }
    }
}
