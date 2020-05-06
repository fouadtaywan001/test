namespace Pusher.Core.Models;

/// <summary>
/// How the project publishes. RealTime = the worker pushes commits over time on the
/// planned schedule. Backdated = everything executes instantly at activation in one
/// batch — each commit signed with its planned past date — no worker involvement.
/// </summary>
public enum ProjectMode
{
    RealTime,   // scheduled, worker-driven
    Backdated,  // instant batch at activation
}

/// <summary>Lifecycle of a project registered in the app.</summary>
public enum ProjectStatus
{
    Draft,      // script saved, not activated
    Active,     // plan activated, worker executing
    Paused,     // user paused; slots frozen
    Completed,  // all commits pushed, hooks done
    Attention,  // needs user action (PAT expired, remote conflict, ...)
}

/// <summary>Two-phase status of a planned commit (03-architecture.md §2.4).</summary>
public enum CommitPlanStatus
{
    Pending,    // not yet built
    Committed,  // commit exists in staging, SHA recorded, not pushed
    Pushed,     // on the remote
    Failed,     // exhausted retries; re-queued before next natural slot
    Skipped,    // removed by a plan revision before execution
}

/// <summary>How a commit's signature date is assigned.</summary>
public enum CommitMode
{
    Forward,    // scheduled future slot
    Backdated,  // past-dated batch commit
}

/// <summary>Missed-slot policy (options.catch_up).</summary>
public enum CatchUpPolicy
{
    Immediate,  // run missed slots at next wake, capped by catch_up_max
    Reschedule, // move missed slots to the next free future day
}

/// <summary>Pre-push scanner mode (options.scan).</summary>
public enum ScanMode
{
    Strict, // findings are errors; block activation
    Warn,   // findings warn; user may override with confirmation
}

/// <summary>split.strategy</summary>
public enum SplitStrategy
{
    Manifest, // explicit ordered commit blocks
    Folders,  // one commit per top-level folder
    Groups,   // files batched into groups of N
}

/// <summary>split.order for auto strategies.</summary>
public enum SplitOrder
{
    AsWritten,
    Alphabetical,
}

/// <summary>repo.visibility (used when create = true).</summary>
public enum RepoVisibility
{
    Private,
    Public,
}

/// <summary>Result of an executed slot.</summary>
public enum SlotResult
{
    None,
    Success,
    Failed,
    Skipped,
}
