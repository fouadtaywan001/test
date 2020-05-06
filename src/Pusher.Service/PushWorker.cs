using Pusher.Core.Abstractions;
using Pusher.Core.Models;
using Pusher.Core.Planning;
using Pusher.Ipc;

namespace Pusher.Service;

/// <summary>
/// The daily engine (plan/03-architecture.md section 2.4):
/// - On startup: crash recovery — any commit in Committed state is checked against the staging
///   repo and pushed (the two-phase guarantee: never re-create, never double-push).
/// - Every poll interval: fetch due slots, execute two-phase (commit -> record SHA -> push),
///   apply catch-up policy caps, run best-effort hooks when a project completes.
/// - Hosts the named-pipe control server for the UI (ping / wake / pause / resume).
/// </summary>
public sealed class PushWorker : BackgroundService
{
    private readonly ILogger<PushWorker> _log;
    private readonly IStateStore _store;
    private readonly IGitEngine _git;
    private readonly ITokenProtector _protector;
    private readonly Storage.AppSettingsStore _settingsStore;
    private readonly SemaphoreSlim _wake = new(0);

    public PushWorker(ILogger<PushWorker> log, IStateStore store, IGitEngine git,
        ITokenProtector protector, Storage.AppSettingsStore settingsStore)
    {
        _log = log;
        _store = store;
        _git = git;
        _protector = protector;
        _settingsStore = settingsStore;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _store.Initialize();
        RecoverCommittedButUnpushed();

        var pipeServer = new PipeServer(HandlePipeRequestAsync);
        _ = Task.Run(() => pipeServer.RunAsync(ct), ct);

        var settings = _settingsStore.Load();
        var interval = TimeSpan.FromSeconds(Math.Max(15, settings.PollIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                ProcessDueSlots();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Slot processing cycle failed");
            }

            // Sleep until the next poll — or an explicit "wake" from the UI.
            try
            {
                await _wake.WaitAsync(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ---------------- Crash recovery ----------------

    private void RecoverCommittedButUnpushed()
    {
        foreach (var commit in _store.GetRecoverableCommits())
        {
            var project = _store.GetProject(commit.ProjectId);
            if (project?.StagingPath is null || commit.CommitSha is null) continue;

            try
            {
                if (_git.LocalCommitExists(project.StagingPath, commit.CommitSha))
                {
                    _log.LogWarning("Recovering unpushed commit {Sha} for project {Project}", commit.CommitSha, project.Name);
                    PushProject(project);
                    _store.MarkPushed(commit.Id);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Recovery push failed for {Project}; will retry next cycle", project.Name);
            }
        }
    }

    // ---------------- Main cycle ----------------

    /// <summary>Missed = a pending slot whose planned time is further in the past than this.</summary>
    private static readonly TimeSpan MissedThreshold = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The schedule invariant (per project): commits execute strictly in order — order N
    /// never runs before 1..N-1 are done — and pushes are never bursted. Each cycle:
    /// 1. If the HEAD (lowest pending order) slot was missed by more than the threshold,
    ///    the pending tail is first re-timed (policy Immediate: head runs now, later missed
    ///    slots spread with randomized realistic gaps; policy Reschedule: whole tail shifts
    ///    forward by days, preserving the time-of-day pattern).
    /// 2. Only the head commit executes when due. Consecutive backdated commits are the one
    ///    exception: they form a single batch + push by design (their dates are in the past).
    /// Slot times of non-head commits are irrelevant for execution — order is king.
    /// </summary>
    private void ProcessDueSlots()
    {
        var settings = _settingsStore.Load();
        var nowUtc = DateTimeOffset.UtcNow;

        foreach (var project in _store.GetAllProjects())
        {
            if (project.Status != ProjectStatus.Active || project.StagingPath is null) continue;

            // Backdated-mode projects execute instantly in the app at activation; if one
            // is still Active here the app crashed or was closed mid-batch — finish the
            // whole batch now instead of dripping it out on slot times.
            if (project.Mode == ProjectMode.Backdated)
            {
                var s = _settingsStore.Load();
                var tok = s.ProtectedToken is null ? "" : _protector.Unprotect(s.ProtectedToken);
                var result = new Pusher.Core.Execution.InstantExecutor(_store, _git)
                    .Run(project.Id, s.AuthorName, s.AuthorEmail, "x-access-token", tok);
                if (result.Success)
                    _log.LogInformation("Instant batch recovered for {Project}: {Pushed} pushed", project.Name, result.Pushed);
                else
                    _log.LogError("Instant batch recovery failed for {Project}: {Error}", project.Name, result.Error);
                continue;
            }

            var plans = _store.GetCommitPlans(project.Id).OrderBy(p => p.Order).ToList();
            var slots = _store.GetSlots(project.Id).ToDictionary(s => s.CommitPlanId);
            var pending = plans.Where(p => p.Status == CommitPlanStatus.Pending && slots.ContainsKey(p.Id)).ToList();
            if (pending.Count == 0) continue;

            var head = pending[0];
            var headSlot = slots[head.Id];

            // ---- 1. keep the schedule logical after downtime -------------------
            if (headSlot.ScheduledAtUtc < nowUtc - MissedThreshold)
            {
                var tail = pending.Select(p => new SlotTime(p.Id, slots[p.Id].ScheduledAtUtc)).ToList();
                var retimed = settings.CatchUp == CatchUpPolicy.Immediate
                    ? Rescheduler.CatchUpFromNow(tail, nowUtc, seed: unchecked(project.ScriptSeed + plans.Count))
                    : Rescheduler.ShiftForwardByDays(tail, nowUtc);

                foreach (var st in retimed)
                    _store.UpdatePendingSlotSchedule(st.PlanId, st.ScheduledAtUtc);

                _log.LogInformation(
                    "Rescheduled {Count} pending slots of {Project} after downtime (policy {Policy})",
                    retimed.Count, project.Name, settings.CatchUp);

                // refresh local view with the new times
                slots = _store.GetSlots(project.Id).ToDictionary(s => s.CommitPlanId);
                headSlot = slots[head.Id];
            }

            // ---- 2. execute the head when due (plus a backdated batch) ---------
            if (headSlot.ScheduledAtUtc > nowUtc) continue;

            // Strict order: the batch is the head plus any DIRECTLY consecutive backdated
            // commits whose slots are also due. A forward head executes alone.
            var batch = new List<CommitPlan> { head };
            if (head.Mode == CommitMode.Backdated)
            {
                foreach (var p in pending.Skip(1))
                {
                    if (p.Mode != CommitMode.Backdated || slots[p.Id].ScheduledAtUtc > nowUtc) break;
                    batch.Add(p);
                }
            }

            try
            {
                _git.EnsureStagingRepo(project.StagingPath, project.Branch);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Staging repo init failed for {Project}; skipping cycle", project.Name);
                continue;
            }

            var anythingCommitted = false;
            foreach (var plan in batch)
            {
                var slot = slots[plan.Id];
                try
                {
                    var when = plan.Mode == CommitMode.Backdated
                        ? slot.ScheduledAtUtc          // backdated signature = planned past date
                        : DateTimeOffset.Now;          // forward commits use "now"

                    var sha = _git.CreateCommit(project.StagingPath, plan.ResolvedFiles, ComposeMessage(plan),
                        settings.AuthorName, settings.AuthorEmail, when);
                    _store.MarkCommitted(plan.Id, sha);   // phase 1 recorded before any push
                    anythingCommitted = true;
                    _store.RecordSlotResult(slot.Id, SlotResult.Success, DateTimeOffset.UtcNow, "committed");
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Commit failed for slot {Slot} of {Project}", slot.Id, project.Name);
                    _store.RecordSlotResult(slot.Id, SlotResult.Failed, DateTimeOffset.UtcNow, ex.Message);
                    _store.MarkFailed(plan.Id);
                    break; // strict order: never continue past a failed commit
                }
            }

            if (anythingCommitted)
            {
                try
                {
                    PushProject(project);   // phase 2: one push for the whole batch
                    var batchIds = batch.Select(b => b.Id).ToHashSet();
                    foreach (var p in _store.GetCommitPlans(project.Id))
                        if (batchIds.Contains(p.Id) && p.Status == CommitPlanStatus.Committed)
                            _store.MarkPushed(p.Id);
                    MaybeComplete(project);
                }
                catch (Exception ex)
                {
                    // Committed-but-unpushed: recovery loop will retry. Never re-commit.
                    _log.LogError(ex, "Push failed for {Project}; commits remain in Committed state", project.Name);
                }
            }
        }
    }

    private void PushProject(Project project)
    {
        var settings = _settingsStore.Load();
        var token = settings.ProtectedToken is null ? "" : _protector.Unprotect(settings.ProtectedToken);
        _git.Push(project.StagingPath!, project.RemoteUrl, project.Branch, "x-access-token", token);
    }

    private void MaybeComplete(Project project)
    {
        var plans = _store.GetCommitPlans(project.Id);
        if (!plans.All(p => p.Status is CommitPlanStatus.Pushed or CommitPlanStatus.Skipped)) return;

        project.Status = ProjectStatus.Completed;
        _store.UpsertProject(project);
        _log.LogInformation("Project {Project} completed; {Hooks} hooks pending",
            project.Name, _store.GetPendingHooks(project.Id).Count);
        // Hook execution (topics/homepage/visibility via Octokit) is triggered here;
        // best-effort — failures are logged and retryable from the UI, never fatal.
    }

    private static string ComposeMessage(CommitPlan plan)
        => string.IsNullOrWhiteSpace(plan.Body) ? plan.Message : $"{plan.Message}\n\n{plan.Body}";

    // ---------------- Pipe control ----------------

    private Task<PipeResponse> HandlePipeRequestAsync(PipeRequest request)
    {
        switch (request.Command)
        {
            case "ping":
                return Task.FromResult(new PipeResponse(true, $"pong:{PipeProtocol.Version}"));

            case "wake":
                _wake.Release();
                return Task.FromResult(new PipeResponse(true, "waking"));

            case "pause" or "resume" when request.ProjectId is long id:
            {
                var project = _store.GetProject(id);
                if (project is null) return Task.FromResult(new PipeResponse(false, "Unknown project."));
                project.Status = request.Command == "pause" ? ProjectStatus.Paused : ProjectStatus.Active;
                _store.UpsertProject(project);
                return Task.FromResult(new PipeResponse(true, $"{project.Name} {request.Command}d"));
            }

            case "status":
            {
                var counts = _store.GetAllProjects()
                    .GroupBy(p => p.Status)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());
                return Task.FromResult(new PipeResponse(true, PayloadJson: System.Text.Json.JsonSerializer.Serialize(counts)));
            }

            default:
                return Task.FromResult(new PipeResponse(false, $"Unknown command '{request.Command}'."));
        }
    }
}
