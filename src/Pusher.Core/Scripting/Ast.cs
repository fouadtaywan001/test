namespace Pusher.Core.Scripting;

/// <summary>
/// Loosely-typed AST for PushScript. The parser guarantees structural shape;
/// the Validator (§8) enforces which keys/types are legal per block, so the AST
/// keeps values as tagged <see cref="AstValue"/> nodes carrying source positions.
/// </summary>
public sealed class ScriptNode
{
    public string Version { get; set; } = "";
    public int VersionLine { get; set; }
    public int VersionColumn { get; set; }
    public List<BlockNode> Blocks { get; } = new();
}

public sealed class BlockNode
{
    public required string Name { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public List<AssignmentNode> Assignments { get; } = new();
    public List<CommitNode> Commits { get; } = new();      // only in `split`
    public List<DayRuleNode> DayRules { get; } = new();    // only in `schedule`
    public List<BlockNode> SubBlocks { get; } = new();     // e.g. hooks.after_last_commit
}

public sealed class AssignmentNode
{
    public required string Key { get; init; }
    public required AstValue Value { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

public sealed class CommitNode
{
    public required string Message { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public List<AssignmentNode> Assignments { get; } = new();
}

public sealed class DayRuleNode
{
    public required DaySelector Selector { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public bool IsSkip { get; init; }
    public List<AssignmentNode> Assignments { get; } = new();
}

public enum DaySelectorKind { Weekdays, Weekends, WeekdayList, SingleDate, DateRange }

public sealed class DaySelector
{
    public required DaySelectorKind Kind { get; init; }
    public List<DayOfWeek> Weekdays { get; } = new();
    public DateOnly? Date { get; init; }
    public DateOnly? DateEnd { get; init; }
}

// ---- value nodes -------------------------------------------------------

public enum AstValueKind
{
    String, Integer, Percent, Bool, Date, Time, Duration, TimeRange,
    List, Object, FuncCall, Enum,
    Pair,      // `key: value` inside a list, e.g. weighted([0: 30%, 1: 70%]) — Items[0]=key, Items[1]=value
    DateRange, // `date .. date` (day rules)
}

public sealed class AstValue
{
    public required AstValueKind Kind { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }

    public string? Str { get; init; }
    public int? Int { get; init; }
    public bool? Bool { get; init; }
    public DateOnly? Date { get; init; }
    public TimeOnly? Time { get; init; }
    public TimeOnly? TimeEnd { get; init; }   // for TimeRange
    public int? DurationMinutes { get; init; }
    public string? EnumName { get; init; }    // enum / identifier literal (e.g. `manifest`, `today`)

    public List<AstValue> Items { get; } = new();          // List
    public List<AssignmentNode> Fields { get; } = new();   // Object
    public string? FuncName { get; init; }                 // FuncCall
    public List<FuncArg> Args { get; } = new();            // FuncCall
}

public sealed class FuncArg
{
    public string? Name { get; init; }        // named arg (e.g. size: 5)
    public required AstValue Value { get; init; }
}
