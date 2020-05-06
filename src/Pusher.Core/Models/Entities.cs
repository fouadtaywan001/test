namespace Pusher.Core.Models;

/// <summary>A registered local project with its PushScript and publish state.</summary>
public sealed class Project
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string LocalPath { get; set; }
    public string? StagingPath { get; set; }
    public required string RemoteUrl { get; set; }
    public string Branch { get; set; } = "main";
    public ProjectStatus Status { get; set; } = ProjectStatus.Draft;
    public ProjectMode Mode { get; set; } = ProjectMode.RealTime;
    public required string ScriptText { get; set; }
    public string ScriptVersion { get; set; } = "1.0";
    public int ScriptSeed { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One planned commit from the split manifest (resolved: concrete files, not globs).</summary>
public sealed class CommitPlan
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public int Order { get; set; }                       // 1-based manifest order
    public required string Message { get; set; }
    public string Body { get; set; } = "";
    public required IReadOnlyList<string> FileGlobs { get; set; }
    public required IReadOnlyList<string> ResolvedFiles { get; set; } // relative paths, snapshot at activation
    public CommitMode Mode { get; set; } = CommitMode.Forward;
    public CommitPlanStatus Status { get; set; } = CommitPlanStatus.Pending;
    public string? CommitSha { get; set; }               // set when Committed (two-phase)
}

/// <summary>A concrete scheduled execution time for one CommitPlan.</summary>
public sealed class Slot
{
    public long Id { get; set; }
    public long CommitPlanId { get; set; }
    public DateTimeOffset ScheduledAtUtc { get; set; }
    public DateTimeOffset? ExecutedAtUtc { get; set; }
    public SlotResult Result { get; set; } = SlotResult.None;
    public string Log { get; set; } = "";
}

/// <summary>Best-effort post-publish action (hooks block). Never blocks the plan.</summary>
public sealed class HookAction
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public required string Kind { get; set; }            // "add_topics" | "set_website" | "make_public"
    public required string PayloadJson { get; set; }
    public CommitPlanStatus Status { get; set; } = CommitPlanStatus.Pending;
}
