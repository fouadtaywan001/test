using Pusher.Core.Models;

namespace Pusher.Core.Scripting;

/// <summary>
/// Fully-typed result of validating a PushScript. Every default from the spec
/// (plan/05-pushscript-dsl.md §6) is already applied — consumers never see nulls
/// for defaulted keys.
/// </summary>
public sealed class ScriptModel
{
    public string Version { get; init; } = "1.0";
    public required RepoConfig Repo { get; init; }
    public required SplitConfig Split { get; init; }
    public ScheduleConfig? Schedule { get; init; }
    public BackdateConfig? Backdate { get; init; }
    public HooksConfig? Hooks { get; init; }
    public OptionsConfig Options { get; init; } = new();
}

public sealed class RepoConfig
{
    public required string Remote { get; init; }
    public string Branch { get; init; } = "main";
    public required string IdentityName { get; init; }
    public required string IdentityEmail { get; init; }
    public bool Create { get; init; }
    public RepoVisibility Visibility { get; init; } = RepoVisibility.Private;
    public string Description { get; init; } = "";
}

public sealed class SplitConfig
{
    public SplitStrategy Strategy { get; init; } = SplitStrategy.Manifest;
    public int GroupSize { get; init; }                  // groups(size: n)
    public SplitOrder Order { get; init; } = SplitOrder.AsWritten;
    public IReadOnlyList<ManifestCommit> Commits { get; init; } = [];
}

public sealed class ManifestCommit
{
    public required string Message { get; init; }
    public string Body { get; init; } = "";
    public required IReadOnlyList<string> Globs { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>per_day value: fixed integer, random(a,b), or weighted([...]).</summary>
public sealed class CountSpec
{
    public enum SpecKind { Fixed, Random, Weighted }
    public SpecKind Kind { get; init; }
    public int Fixed { get; init; }
    public int Min { get; init; }
    public int Max { get; init; }
    public IReadOnlyList<(int Value, int Percent)> Weights { get; init; } = [];

    public static CountSpec Of(int n) => new() { Kind = SpecKind.Fixed, Fixed = n };
    public static CountSpec RandomOf(int min, int max) => new() { Kind = SpecKind.Random, Min = min, Max = max };
    public static CountSpec WeightedOf(IReadOnlyList<(int, int)> weights) => new() { Kind = SpecKind.Weighted, Weights = weights };

    /// <summary>Largest value this spec can roll — used for feasibility checks (V-SCHED-3).</summary>
    public int MaxValue => Kind switch
    {
        SpecKind.Fixed => Fixed,
        SpecKind.Random => Max,
        _ => Weights.Count == 0 ? 0 : Weights.Max(w => w.Value),
    };

    /// <summary>Smallest value this spec can roll.</summary>
    public int MinValue => Kind switch
    {
        SpecKind.Fixed => Fixed,
        SpecKind.Random => Min,
        _ => Weights.Count == 0 ? 0 : Weights.Min(w => w.Value),
    };
}

public sealed class ScheduleConfig
{
    public DateOnly? Start { get; init; }                // null = `today` (resolved at planning time)
    public bool StartIsToday { get; init; }
    public DateOnly? End { get; init; }
    public CountSpec PerDay { get; init; } = CountSpec.RandomOf(1, 3);
    public TimeOnly ActiveFrom { get; init; } = new(9, 0);
    public TimeOnly ActiveTo { get; init; } = new(22, 0);
    public int JitterMinutes { get; init; } = 30;
    public int RestDaysPercent { get; init; }
    public int MinGapMinutes { get; init; } = 20;
    public IReadOnlyList<DayRule> DayRules { get; init; } = [];
}

/// <summary>A resolved `on <selector> { ... }` rule; later rules win.</summary>
public sealed class DayRule
{
    public required DaySelector Selector { get; init; }
    public bool IsSkip { get; init; }
    public CountSpec? PerDay { get; init; }
    public TimeOnly? ActiveFrom { get; init; }
    public TimeOnly? ActiveTo { get; init; }
    public int? JitterMinutes { get; init; }
    public int? RestDaysPercent { get; init; }
    public int? MinGapMinutes { get; init; }

    public bool Matches(DateOnly day) => Selector.Kind switch
    {
        DaySelectorKind.Weekdays => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        DaySelectorKind.Weekends => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
        DaySelectorKind.WeekdayList => Selector.Weekdays.Contains(day.DayOfWeek),
        DaySelectorKind.SingleDate => day == Selector.Date,
        DaySelectorKind.DateRange => day >= Selector.Date && day <= Selector.DateEnd,
        _ => false,
    };
}

public sealed class BackdateConfig
{
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public CountSpec PerDay { get; init; } = CountSpec.RandomOf(1, 3);
    public TimeOnly ActiveFrom { get; init; } = new(9, 0);
    public TimeOnly ActiveTo { get; init; } = new(22, 0);
    public int RestDaysPercent { get; init; }
    public int MinGapMinutes { get; init; } = 20;
    /// <summary>Number of manifest commits consumed (`first n`); null = `all`.</summary>
    public int? CommitCount { get; init; }
}

public sealed class HooksConfig
{
    public IReadOnlyList<string> AddTopics { get; init; } = [];
    public string? SetWebsite { get; init; }
    public bool MakePublic { get; init; }
}

public sealed class OptionsConfig
{
    public CatchUpPolicy? CatchUp { get; init; }         // null = use global setting
    public int CatchUpMax { get; init; } = 3;
    public int? Seed { get; init; }                      // null = random at activation
    public ScanMode Scan { get; init; } = ScanMode.Strict;
}
