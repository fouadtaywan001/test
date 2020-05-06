using Pusher.Core.Abstractions;
using Pusher.Core.Models;

namespace Pusher.Core.Execution;

/// <summary>Progress callback payload for UI feedback during an instant batch run.</summary>
public sealed record InstantProgress(int Done, int Total, string CurrentMessage);

/// <summary>Outcome of an instant batch execution.</summary>
public sealed record InstantResult(bool Success, int Committed, int Pushed, string? Error);

/// <summary>
/// Executes a Backdated-mode project in ONE shot: every pending commit is created in
/// manifest order — each signed with its planned (past) slot date — then a single push
/// publishes the whole batch. The two-phase guarantee is kept: each commit is recorded
/// as Committed with its SHA before the push, so a crash mid-batch is recoverable
/// (never re-create, never double-push) by simply calling Run again.
/// Shared by the app (at activation), the worker (crash recovery), and the e2e harness.
/// </summary>
public sealed class InstantExecutor
{
    private readonly IStateStore _store;
    private readonly IGitEngine _git;

    public InstantExecutor(IStateStore store, IGitEngine git)
    {
        _store = store;
        _git = git;
    }

    /// <summary>
    /// Runs the full pending batch for <paramref name="projectId"/>. Idempotent:
    /// already-Committed plans are not re-created (their SHA is verified instead)
    /// and already-Pushed plans are skipped entirely.
    /// </summary>
    public InstantResult Run(long projectId, string authorName, string authorEmail,
        string pushUsername, string pushToken, Action<InstantProgress>? onProgress = null)
    {
        var project = _store.GetProject(projectId);
        if (project is null) return new InstantResult(false, 0, 0, "Project not found.");
        if (project.StagingPath is null) return new InstantResult(false, 0, 0, "Project has no staging snapshot.");

        var plans = _store.GetCommitPlans(projectId).OrderBy(p => p.Order).ToList();
        var slots = _store.GetSlots(projectId).ToDictionary(s => s.CommitPlanId);
        var todo = plans.Where(p => p.Status is CommitPlanStatus.Pending or CommitPlanStatus.Committed).ToList();
        if (todo.Count == 0) return new InstantResult(true, 0, 0, null);

        try
        {
            _git.EnsureStagingRepo(project.StagingPath, project.Branch);
        }
        catch (Exception ex)
        {
            return new InstantResult(false, 0, 0, $"Staging repo init failed: {ex.Message}");
        }

        int committed = 0, done = 0;
        foreach (var plan in todo)
        {
            onProgress?.Invoke(new InstantProgress(done, todo.Count, plan.Message));

            // Crash recovery: a Committed plan whose SHA exists locally is not re-created.
            if (plan.Status == CommitPlanStatus.Committed && plan.CommitSha is not null
                && _git.LocalCommitExists(project.StagingPath, plan.CommitSha))
            {
                done++;
                continue;
            }

            if (!slots.TryGetValue(plan.Id, out var slot))
                return new InstantResult(false, committed, 0, $"Commit {plan.Order} has no slot.");

            try
            {
                // Backdated signature = the planned past date; forward plans (mixed
                // scripts) use "now" — dates stay monotonic because the planner
                // guarantees backdate < schedule ordering.
                var when = plan.Mode == CommitMode.Backdated ? slot.ScheduledAtUtc : DateTimeOffset.Now;
                var message = string.IsNullOrWhiteSpace(plan.Body) ? plan.Message : $"{plan.Message}\n\n{plan.Body}";
                var sha = _git.CreateCommit(project.StagingPath, plan.ResolvedFiles, message,
                    authorName, authorEmail, when);
                _store.MarkCommitted(plan.Id, sha);
                _store.RecordSlotResult(slot.Id, SlotResult.Success, DateTimeOffset.UtcNow, "committed (instant batch)");
                committed++;
                done++;
            }
            catch (Exception ex)
            {
                _store.RecordSlotResult(slot.Id, SlotResult.Failed, DateTimeOffset.UtcNow, ex.Message);
                _store.MarkFailed(plan.Id);
                return new InstantResult(false, committed, 0,
                    $"Commit {plan.Order} ('{plan.Message}') failed: {ex.Message}");
            }
        }

        onProgress?.Invoke(new InstantProgress(done, todo.Count, "Pushing to GitHub…"));

        try
        {
            _git.Push(project.StagingPath, project.RemoteUrl, project.Branch, pushUsername, pushToken);
        }
        catch (Exception ex)
        {
            // Committed-but-unpushed: recoverable — rerunning Run pushes without re-creating.
            return new InstantResult(false, committed, 0, $"Push failed: {ex.Message}");
        }

        int pushed = 0;
        foreach (var plan in _store.GetCommitPlans(projectId))
        {
            if (plan.Status == CommitPlanStatus.Committed)
            {
                _store.MarkPushed(plan.Id);
                pushed++;
            }
        }

        var refreshed = _store.GetProject(projectId)!;
        if (_store.GetCommitPlans(projectId).All(p => p.Status is CommitPlanStatus.Pushed or CommitPlanStatus.Skipped))
        {
            refreshed.Status = ProjectStatus.Completed;
            _store.UpsertProject(refreshed);
        }

        return new InstantResult(true, committed, pushed, null);
    }
}
