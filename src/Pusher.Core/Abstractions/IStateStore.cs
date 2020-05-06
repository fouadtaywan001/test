using Pusher.Core.Models;

namespace Pusher.Core.Abstractions;

/// <summary>
/// Persistence for projects, commit plans, slots, and hooks (SQLite in Pusher.Storage).
/// Commit state machine: Pending -> Committed -> Pushed (or Failed/Skipped);
/// the Committed intermediate state is what makes crash recovery possible
/// (plan/03-architecture.md section 2.4).
/// </summary>
public interface IStateStore
{
    /// <summary>Create/migrate the schema (schema_version pragma; forward-only migrations).</summary>
    void Initialize();

    // ---- Projects ----
    Project? GetProject(long id);
    IReadOnlyList<Project> GetAllProjects();
    long UpsertProject(Project project);
    /// <summary>Removes the project, its plans/slots/hooks, and is followed by staging cleanup.
    /// Never touches the live folder or the GitHub repo.</summary>
    void DeleteProject(long id);

    // ---- Commit plans ----
    IReadOnlyList<CommitPlan> GetCommitPlans(long projectId);
    void SaveCommitPlans(long projectId, IReadOnlyList<CommitPlan> plans);
    /// <summary>Commits in Committed state with a recorded SHA but no Pushed transition —
    /// crash-recovery scan at worker startup.</summary>
    IReadOnlyList<CommitPlan> GetRecoverableCommits();
    void MarkCommitted(long commitPlanId, string sha);
    void MarkPushed(long commitPlanId);
    void MarkFailed(long commitPlanId);
    void MarkSkipped(long commitPlanId);
    /// <summary>Requeue every Failed commit of the project: plan back to Pending (SHA cleared)
    /// and its slot back to no-result so the next worker cycle picks it up as due.</summary>
    void ResetFailed(long projectId);
    /// <summary>Deletes every still-Pending commit plan of the project together with its
    /// slots. Executed history (Pushed/Failed/Skipped/Committed) is never touched.
    /// Used by script re-editing: pending work is replaced by the new plan.</summary>
    void DeletePendingPlans(long projectId);
    /// <summary>Edit the message of a still-Pending commit. No-op if it already ran.</summary>
    void UpdatePendingCommitMessage(long commitPlanId, string message);
    /// <summary>Move the scheduled time of a still-Pending commit's slot. No-op if it already ran.</summary>
    void UpdatePendingSlotSchedule(long commitPlanId, DateTimeOffset scheduledAtUtc);

    // ---- Slots ----
    IReadOnlyList<Slot> GetSlots(long projectId);
    void SaveSlots(long projectId, IReadOnlyList<Slot> slots);
    /// <summary>Slots scheduled at or before <paramref name="asOfUtc"/> whose commit is still Pending.</summary>
    IReadOnlyList<Slot> GetDueSlots(DateTimeOffset asOfUtc);
    void RecordSlotResult(long slotId, SlotResult result, DateTimeOffset executedAtUtc, string log);

    // ---- Hooks ----
    IReadOnlyList<HookAction> GetPendingHooks(long projectId);
    void SaveHooks(long projectId, IReadOnlyList<HookAction> hooks);
    void SetHookStatus(long hookId, CommitPlanStatus status);
}
