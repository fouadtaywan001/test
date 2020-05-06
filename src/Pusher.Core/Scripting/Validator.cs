using System.Text.RegularExpressions;
using Pusher.Core.Models;

namespace Pusher.Core.Scripting;

/// <summary>
/// Lowers the parsed AST to a typed <see cref="ScriptModel"/>, enforcing the
/// full validation catalog from plan/05-pushscript-dsl.md §8.1–8.3:
/// E002/E003/E005 structural-semantic, V-REPO, V-SPLIT-1 (script part),
/// V-SCHED, V-BACK, and cross-block V-CROSS-1..3.
/// File-system checks (glob resolution, V-SPLIT-2/3) live in SplitResolver;
/// environment checks (§8.4) and V-CROSS-4 run at activation.
/// </summary>
public sealed partial class Validator
{
    private static readonly string[] KnownBlocks = ["repo", "split", "schedule", "backdate", "hooks", "options"];

    [GeneratedRegex(@"^(https://github\.com/[^/\s]+/[^/\s]+?(\.git)?|git@github\.com:[^/\s]+/[^/\s]+?(\.git)?)$")]
    private static partial Regex GitHubRemoteRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    private readonly DiagnosticBag _diags;
    private readonly DateOnly _today;

    public Validator(DiagnosticBag diagnostics, DateOnly today)
    {
        _diags = diagnostics;
        _today = today;
    }

    /// <summary>Returns the typed model, or null when errors make binding impossible.</summary>
    public ScriptModel? Validate(ScriptNode ast)
    {
        foreach (var block in ast.Blocks)
            if (!KnownBlocks.Contains(block.Name))
                _diags.Error("E002", $"Unknown block '{block.Name}'. Valid blocks: {string.Join(", ", KnownBlocks)}.", block.Line, block.Column);

        var repoBlock = ast.Blocks.FirstOrDefault(b => b.Name == "repo");
        var splitBlock = ast.Blocks.FirstOrDefault(b => b.Name == "split");
        var scheduleBlock = ast.Blocks.FirstOrDefault(b => b.Name == "schedule");
        var backdateBlock = ast.Blocks.FirstOrDefault(b => b.Name == "backdate");
        var hooksBlock = ast.Blocks.FirstOrDefault(b => b.Name == "hooks");
        var optionsBlock = ast.Blocks.FirstOrDefault(b => b.Name == "options");

        if (repoBlock is null)
            _diags.Error("E002", "Missing required block 'repo'.", 1, 1);
        if (splitBlock is null)
            _diags.Error("E002", "Missing required block 'split'.", 1, 1);
        if (scheduleBlock is null && backdateBlock is null)
            _diags.Error("E002", "Missing 'schedule' block ('schedule' may be omitted only in a pure-backfill script with a 'backdate' block).", 1, 1);

        var repo = repoBlock is null ? null : BindRepo(repoBlock);
        var split = splitBlock is null ? null : BindSplit(splitBlock);
        var schedule = scheduleBlock is null ? null : BindSchedule(scheduleBlock);
        var backdate = backdateBlock is null ? null : BindBackdate(backdateBlock);
        var hooks = hooksBlock is null ? null : BindHooks(hooksBlock);
        var options = optionsBlock is null ? new OptionsConfig() : BindOptions(optionsBlock);

        if (repo is not null && split is not null)
            CrossValidate(repo, split, schedule, backdate, hooks,
                scheduleBlock, backdateBlock, hooksBlock);

        if (_diags.HasErrors || repo is null || split is null)
            return null;

        return new ScriptModel
        {
            Version = ast.Version,
            Repo = repo,
            Split = split,
            Schedule = schedule,
            Backdate = backdate,
            Hooks = hooks,
            Options = options,
        };
    }

    // ---- repo -------------------------------------------------------------

    private RepoConfig? BindRepo(BlockNode block)
    {
        string? remote = null, name = null, email = null, description = "";
        string branch = "main";
        bool create = false;
        var visibility = RepoVisibility.Private;

        foreach (var a in block.Assignments)
        {
            switch (a.Key)
            {
                case "remote": remote = ExpectString(a); break;
                case "branch": branch = ExpectString(a) ?? branch; break;
                case "create": create = ExpectBool(a) ?? create; break;
                case "description": description = ExpectString(a) ?? ""; break;
                case "visibility":
                    visibility = ExpectEnum(a, ["private", "public"]) switch
                    {
                        "public" => RepoVisibility.Public,
                        _ => RepoVisibility.Private,
                    };
                    break;
                case "identity":
                    if (a.Value.Kind != AstValueKind.Object)
                    {
                        _diags.Error("E003", "'identity' must be an object: { name = \"...\", email = \"...\" }.", a.Line, a.Column);
                        break;
                    }
                    foreach (var f in a.Value.Fields)
                    {
                        switch (f.Key)
                        {
                            case "name": name = ExpectString(f); break;
                            case "email": email = ExpectString(f); break;
                            default: _diags.Error("E003", $"Unknown key '{f.Key}' in identity (valid: name, email).", f.Line, f.Column); break;
                        }
                    }
                    break;
                default:
                    UnknownKey(a, "repo", ["remote", "branch", "identity", "create", "visibility", "description"]);
                    break;
            }
        }

        if (remote is null)
            _diags.Error("E003", "repo.remote is required.", block.Line, block.Column);
        else if (!GitHubRemoteRegex().IsMatch(remote))
            _diags.Error("V-REPO-1", $"'{remote}' is not a GitHub HTTPS or SSH remote URL.", block.Line, block.Column);

        if (name is null || email is null)
            _diags.Error("E003", "repo.identity with both name and email is required.", block.Line, block.Column);
        else if (!EmailRegex().IsMatch(email))
            _diags.Error("V-REPO-2", $"identity.email '{email}' is not a well-formed email address.", block.Line, block.Column);

        if (remote is null || name is null || email is null) return null;

        return new RepoConfig
        {
            Remote = remote,
            Branch = branch,
            IdentityName = name,
            IdentityEmail = email,
            Create = create,
            Visibility = visibility,
            Description = description ?? "",
        };
    }

    // ---- split ------------------------------------------------------------

    private SplitConfig? BindSplit(BlockNode block)
    {
        var strategy = SplitStrategy.Manifest;
        int groupSize = 0;
        var order = SplitOrder.AsWritten;

        foreach (var a in block.Assignments)
        {
            switch (a.Key)
            {
                case "strategy":
                    if (a.Value is { Kind: AstValueKind.FuncCall, FuncName: "groups" })
                    {
                        strategy = SplitStrategy.Groups;
                        var sizeArg = a.Value.Args.FirstOrDefault(x => x.Name == "size") ?? a.Value.Args.FirstOrDefault();
                        int? size = sizeArg?.Value.Kind == AstValueKind.Integer ? sizeArg.Value.Int : null;
                        if (size is null or < 1)
                            _diags.Error("E005", "groups(size: n) requires an integer size ≥ 1.", a.Line, a.Column);
                        else
                            groupSize = size.Value;
                    }
                    else
                    {
                        strategy = ExpectEnum(a, ["manifest", "folders"]) switch
                        {
                            "folders" => SplitStrategy.Folders,
                            _ => SplitStrategy.Manifest,
                        };
                    }
                    break;
                case "order":
                    order = ExpectEnum(a, ["as_written", "alphabetical"]) switch
                    {
                        "alphabetical" => SplitOrder.Alphabetical,
                        _ => SplitOrder.AsWritten,
                    };
                    break;
                default:
                    UnknownKey(a, "split", ["strategy", "order"]);
                    break;
            }
        }

        var commits = new List<ManifestCommit>();
        foreach (var c in block.Commits)
        {
            List<string> globs = [];
            string body = "";
            foreach (var a in c.Assignments)
            {
                switch (a.Key)
                {
                    case "files": globs = ExpectStringList(a); break;
                    case "message": body = ExpectString(a) ?? ""; break;
                    default: UnknownKey(a, "commit", ["files", "message"]); break;
                }
            }
            if (globs.Count == 0)
                _diags.Error("V-SPLIT-1", $"Commit \"{c.Message}\" has no 'files' globs (a non-empty list is required).", c.Line, c.Column);
            commits.Add(new ManifestCommit { Message = c.Message, Body = body, Globs = globs, Line = c.Line, Column = c.Column });
        }

        if (strategy == SplitStrategy.Manifest && commits.Count == 0)
            _diags.Error("V-SPLIT-1", "split with strategy = manifest requires at least one commit block.", block.Line, block.Column);
        if (strategy != SplitStrategy.Manifest && commits.Count > 0)
            _diags.Warn("E003", "commit blocks are ignored when strategy is not 'manifest'.", block.Line, block.Column);

        return new SplitConfig { Strategy = strategy, GroupSize = groupSize, Order = order, Commits = commits };
    }

    // ---- schedule -----------------------------------------------------------

    private ScheduleConfig BindSchedule(BlockNode block)
    {
        DateOnly? start = null, end = null;
        bool startIsToday = false;
        var perDay = CountSpec.RandomOf(1, 3);
        TimeOnly activeFrom = new(9, 0), activeTo = new(22, 0);
        int jitter = 30, restDays = 0, minGap = 20;

        foreach (var a in block.Assignments)
        {
            switch (a.Key)
            {
                case "start":
                    if (a.Value is { Kind: AstValueKind.Enum, EnumName: "today" }) startIsToday = true;
                    else start = ExpectDate(a);
                    break;
                case "end": end = ExpectDate(a); break;
                case "per_day": perDay = ExpectCountSpec(a) ?? perDay; break;
                case "active_hours": (activeFrom, activeTo) = ExpectTimeRange(a) ?? (activeFrom, activeTo); break;
                case "jitter": jitter = ExpectDuration(a) ?? jitter; break;
                case "rest_days": restDays = ExpectPercent(a) ?? restDays; break;
                case "min_gap": minGap = ExpectDuration(a) ?? minGap; break;
                default:
                    UnknownKey(a, "schedule", ["start", "end", "per_day", "active_hours", "jitter", "rest_days", "min_gap"]);
                    break;
            }
        }

        if (start is null && !startIsToday)
            _diags.Error("E003", "schedule.start is required (a date or 'today').", block.Line, block.Column);

        // V-SCHED-1
        if (start is not null && start < _today)
            _diags.Error("V-SCHED-1", $"schedule.start ({start:yyyy-MM-dd}) must be today or later.", block.Line, block.Column);
        var effectiveStart = startIsToday ? _today : start;
        if (end is not null && effectiveStart is not null && end <= effectiveStart)
            _diags.Error("V-SCHED-1", $"schedule.end ({end:yyyy-MM-dd}) must be after start.", block.Line, block.Column);

        // V-SCHED-2
        ValidateWindow(activeFrom, activeTo, jitter, minGap, block, "schedule");

        var rules = BindDayRules(block);

        return new ScheduleConfig
        {
            Start = start,
            StartIsToday = startIsToday,
            End = end,
            PerDay = perDay,
            ActiveFrom = activeFrom,
            ActiveTo = activeTo,
            JitterMinutes = jitter,
            RestDaysPercent = restDays,
            MinGapMinutes = minGap,
            DayRules = rules,
        };
    }

    private List<DayRule> BindDayRules(BlockNode block)
    {
        var rules = new List<DayRule>();
        foreach (var r in block.DayRules)
        {
            CountSpec? perDay = null;
            TimeOnly? from = null, to = null;
            int? jitter = null, rest = null, gap = null;

            foreach (var a in r.Assignments)
            {
                switch (a.Key)
                {
                    case "per_day": perDay = ExpectCountSpec(a); break;
                    case "active_hours":
                        var range = ExpectTimeRange(a);
                        if (range is not null) (from, to) = range.Value;
                        break;
                    case "jitter": jitter = ExpectDuration(a); break;
                    case "rest_days": rest = ExpectPercent(a); break;
                    case "min_gap": gap = ExpectDuration(a); break;
                    default:
                        UnknownKey(a, "day rule", ["per_day", "active_hours", "jitter", "rest_days", "min_gap"]);
                        break;
                }
            }

            rules.Add(new DayRule
            {
                Selector = r.Selector,
                IsSkip = r.IsSkip,
                PerDay = perDay,
                ActiveFrom = from,
                ActiveTo = to,
                JitterMinutes = jitter,
                RestDaysPercent = rest,
                MinGapMinutes = gap,
            });
        }
        return rules;
    }

    // ---- backdate -----------------------------------------------------------

    private BackdateConfig? BindBackdate(BlockNode block)
    {
        DateOnly? from = null, to = null;
        var perDay = CountSpec.RandomOf(1, 3);
        TimeOnly activeFrom = new(9, 0), activeTo = new(22, 0);
        int restDays = 0, minGap = 20;
        int? commitCount = null;
        bool commitsAll = false;

        foreach (var a in block.Assignments)
        {
            switch (a.Key)
            {
                case "from": from = ExpectDate(a); break;
                case "to": to = ExpectDate(a); break;
                case "per_day": perDay = ExpectCountSpec(a) ?? perDay; break;
                case "active_hours": (activeFrom, activeTo) = ExpectTimeRange(a) ?? (activeFrom, activeTo); break;
                case "rest_days": restDays = ExpectPercent(a) ?? restDays; break;
                case "min_gap": minGap = ExpectDuration(a) ?? minGap; break;
                case "commits":
                    if (a.Value is { Kind: AstValueKind.FuncCall, FuncName: "first" } fc &&
                        fc.Args.Count == 1 && fc.Args[0].Value.Int is int n)
                        commitCount = n;
                    else if (a.Value is { Kind: AstValueKind.Enum, EnumName: "all" })
                        commitsAll = true;
                    else
                        _diags.Error("E003", "backdate.commits must be 'first n' or 'all'.", a.Line, a.Column);
                    break;
                default:
                    UnknownKey(a, "backdate", ["from", "to", "per_day", "active_hours", "rest_days", "min_gap", "commits"]);
                    break;
            }
        }

        if (from is null || to is null)
        {
            _diags.Error("E003", "backdate requires both 'from' and 'to' dates.", block.Line, block.Column);
            return null;
        }

        // V-BACK-1
        if (from >= to)
            _diags.Error("V-BACK-1", $"backdate.from ({from:yyyy-MM-dd}) must be before backdate.to ({to:yyyy-MM-dd}).", block.Line, block.Column);
        if (to > _today)
            _diags.Error("V-BACK-1", $"backdate.to ({to:yyyy-MM-dd}) must not be in the future.", block.Line, block.Column);

        ValidateWindow(activeFrom, activeTo, 0, minGap, block, "backdate");

        return new BackdateConfig
        {
            From = from.Value,
            To = to.Value,
            PerDay = perDay,
            ActiveFrom = activeFrom,
            ActiveTo = activeTo,
            RestDaysPercent = restDays,
            MinGapMinutes = minGap,
            CommitCount = commitsAll ? null : commitCount,
        };
    }

    // ---- hooks / options ------------------------------------------------------

    private HooksConfig BindHooks(BlockNode block)
    {
        List<string> topics = [];
        string? website = null;
        bool makePublic = false;

        foreach (var sub in block.SubBlocks)
        {
            if (sub.Name != "after_last_commit")
            {
                _diags.Error("E003", $"Unknown hook '{sub.Name}' (v1 supports only 'after_last_commit').", sub.Line, sub.Column);
                continue;
            }
            foreach (var a in sub.Assignments)
            {
                switch (a.Key)
                {
                    case "add_topics": topics = ExpectStringList(a); break;
                    case "set_website": website = ExpectString(a); break;
                    case "make_public": makePublic = ExpectBool(a) ?? false; break;
                    default:
                        UnknownKey(a, "after_last_commit", ["add_topics", "set_website", "make_public"]);
                        break;
                }
            }
        }

        foreach (var a in block.Assignments)
            _diags.Error("E003", $"Key '{a.Key}' must be inside an 'after_last_commit {{ ... }}' sub-block.", a.Line, a.Column);

        return new HooksConfig { AddTopics = topics, SetWebsite = website, MakePublic = makePublic };
    }

    private OptionsConfig BindOptions(BlockNode block)
    {
        CatchUpPolicy? catchUp = null;
        int catchUpMax = 3;
        int? seed = null;
        var scan = ScanMode.Strict;

        foreach (var a in block.Assignments)
        {
            switch (a.Key)
            {
                case "catch_up":
                    catchUp = ExpectEnum(a, ["immediate", "reschedule"]) switch
                    {
                        "immediate" => CatchUpPolicy.Immediate,
                        "reschedule" => CatchUpPolicy.Reschedule,
                        _ => null,
                    };
                    break;
                case "catch_up_max": catchUpMax = ExpectInt(a) ?? catchUpMax; break;
                case "seed": seed = ExpectInt(a); break;
                case "scan":
                    scan = ExpectEnum(a, ["strict", "warn"]) switch
                    {
                        "warn" => ScanMode.Warn,
                        _ => ScanMode.Strict,
                    };
                    break;
                default:
                    UnknownKey(a, "options", ["catch_up", "catch_up_max", "seed", "scan"]);
                    break;
            }
        }

        return new OptionsConfig { CatchUp = catchUp, CatchUpMax = catchUpMax, Seed = seed, Scan = scan };
    }

    // ---- cross-block (V-CROSS + V-BACK-2 + V-SCHED-3) ---------------------------

    private void CrossValidate(
        RepoConfig repo, SplitConfig split, ScheduleConfig? schedule, BackdateConfig? backdate, HooksConfig? hooks,
        BlockNode? scheduleBlock, BlockNode? backdateBlock, BlockNode? hooksBlock)
    {
        int manifestCount = split.Commits.Count;

        if (backdate is not null && backdateBlock is not null)
        {
            // V-BACK-2
            if (backdate.CommitCount is int n)
            {
                if (n <= 0 || n > manifestCount)
                    _diags.Error("V-BACK-2", $"backdate.commits = first {n} is out of range (manifest has {manifestCount} commits).", backdateBlock.Line, backdateBlock.Column);
            }
            else if (schedule is not null)
            {
                _diags.Error("V-BACK-2", "backdate.commits = all is only valid when there is no 'schedule' block; use 'first n'.", backdateBlock.Line, backdateBlock.Column);
            }

            // V-CROSS-1 / V-CROSS-2
            if (schedule is not null && scheduleBlock is not null)
            {
                var start = schedule.StartIsToday ? _today : schedule.Start;
                if (start is not null && backdate.To >= start)
                    _diags.Error("V-CROSS-1", $"backdate.to ({backdate.To:yyyy-MM-dd}) must be before schedule.start ({start:yyyy-MM-dd}).", backdateBlock.Line, backdateBlock.Column);

                if (backdate.CommitCount is int cn && cn >= manifestCount)
                    _diags.Error("V-CROSS-2", $"With both backdate and schedule present, backdate.commits must leave at least one forward commit (first n with n < {manifestCount}).", backdateBlock.Line, backdateBlock.Column);
            }
        }

        // V-CROSS-3
        if (hooks is { MakePublic: true } && hooksBlock is not null)
        {
            if (repo.Visibility != RepoVisibility.Private)
                _diags.Error("V-CROSS-3", "hooks.make_public = true requires the repo to be (created) private.", hooksBlock.Line, hooksBlock.Column);
        }

        // V-SCHED-3 feasibility
        if (schedule is not null && scheduleBlock is not null && manifestCount > 0)
        {
            int forwardCount = manifestCount - (backdate?.CommitCount ?? 0);
            int maxPerDay = Math.Max(schedule.PerDay.MaxValue,
                schedule.DayRules.Where(r => !r.IsSkip && r.PerDay is not null).Select(r => r.PerDay!.MaxValue).DefaultIfEmpty(0).Max());

            if (maxPerDay <= 0 && forwardCount > 0)
            {
                _diags.Error("V-SCHED-3", "per_day can never roll above 0 — the schedule can never publish any commit.", scheduleBlock.Line, scheduleBlock.Column);
            }
            else if (schedule.End is not null && forwardCount > 0)
            {
                var start = schedule.StartIsToday ? _today : schedule.Start ?? _today;
                int days = schedule.End.Value.DayNumber - start.DayNumber + 1;
                if (days > 0 && (long)days * maxPerDay < forwardCount)
                    _diags.Error("V-SCHED-3", $"The window {start:yyyy-MM-dd}..{schedule.End:yyyy-MM-dd} can hold at most {days * maxPerDay} commits but {forwardCount} must be scheduled. Extend 'end' or raise 'per_day'.", scheduleBlock.Line, scheduleBlock.Column);
            }
            else if (schedule.End is null && forwardCount > 0 && maxPerDay > 0)
            {
                int minDays = (int)Math.Ceiling(forwardCount / (double)maxPerDay);
                if (minDays > 365)
                    _diags.Warn("V-SCHED-3", $"This plan needs at least {minDays} days to finish — over a year. Consider raising per_day.", scheduleBlock.Line, scheduleBlock.Column);
            }
        }
    }

    // ---- shared window check (V-SCHED-2) ------------------------------------

    private void ValidateWindow(TimeOnly from, TimeOnly to, int jitterMinutes, int minGapMinutes, BlockNode block, string blockName)
    {
        if (from >= to)
        {
            _diags.Error("V-SCHED-2", $"{blockName}.active_hours start must be before end.", block.Line, block.Column);
            return;
        }
        double window = (to - from).TotalMinutes;
        if (jitterMinutes * 2 >= window)
            _diags.Error("V-SCHED-2", $"{blockName}.jitter ({jitterMinutes}m) is too large for the active window ({window:0}m) — slots could land outside it.", block.Line, block.Column);
        if (minGapMinutes >= window)
            _diags.Error("V-SCHED-2", $"{blockName}.min_gap ({minGapMinutes}m) exceeds the active window ({window:0}m).", block.Line, block.Column);
    }

    // ---- typed value readers (E003/E005) ------------------------------------

    private void UnknownKey(AssignmentNode a, string blockName, string[] valid)
        => _diags.Warn("E002", $"Unknown key '{a.Key}' in {blockName} (valid: {string.Join(", ", valid)}). It will be ignored.", a.Line, a.Column);

    private string? ExpectString(AssignmentNode a)
    {
        if (a.Value.Kind == AstValueKind.String) return a.Value.Str;
        _diags.Error("E003", $"'{a.Key}' must be a string.", a.Line, a.Column);
        return null;
    }

    private bool? ExpectBool(AssignmentNode a)
    {
        if (a.Value.Kind == AstValueKind.Bool) return a.Value.Bool;
        _diags.Error("E003", $"'{a.Key}' must be true or false.", a.Line, a.Column);
        return null;
    }

    private int? ExpectInt(AssignmentNode a)
    {
        if (a.Value.Kind == AstValueKind.Integer) return a.Value.Int;
        _diags.Error("E003", $"'{a.Key}' must be an integer.", a.Line, a.Column);
        return null;
    }

    private int? ExpectPercent(AssignmentNode a)
    {
        if (a.Value.Kind == AstValueKind.Percent) return a.Value.Int;
        _diags.Error("E003", $"'{a.Key}' must be a percent value like 15%.", a.Line, a.Column);
        return null;
    }

    private DateOnly? ExpectDate(AssignmentNode a)
    {
        if (a.Value.Kind == AstValueKind.Date) return a.Value.Date;
        _diags.Error("E003", $"'{a.Key}' must be a date (YYYY-MM-DD).", a.Line, a.Column);
        return null;
    }

    private int? ExpectDuration(AssignmentNode a)
    {
        if (a.Value.Kind == AstValueKind.Duration) return a.Value.DurationMinutes;
        _diags.Error("E003", $"'{a.Key}' must be a duration like 45m, 2h or 1d.", a.Line, a.Column);
        return null;
    }

    private (TimeOnly, TimeOnly)? ExpectTimeRange(AssignmentNode a)
    {
        if (a.Value is { Kind: AstValueKind.TimeRange, Time: not null, TimeEnd: not null })
            return (a.Value.Time.Value, a.Value.TimeEnd.Value);
        _diags.Error("E003", $"'{a.Key}' must be a time range like 09:30 .. 22:00.", a.Line, a.Column);
        return null;
    }

    private string? ExpectEnum(AssignmentNode a, string[] allowed)
    {
        if (a.Value.Kind == AstValueKind.Enum && allowed.Contains(a.Value.EnumName))
            return a.Value.EnumName;
        _diags.Error("E003", $"'{a.Key}' must be one of: {string.Join(", ", allowed)}.", a.Line, a.Column);
        return null;
    }

    private List<string> ExpectStringList(AssignmentNode a)
    {
        var result = new List<string>();
        if (a.Value.Kind != AstValueKind.List)
        {
            _diags.Error("E003", $"'{a.Key}' must be a list of strings.", a.Line, a.Column);
            return result;
        }
        foreach (var item in a.Value.Items)
        {
            if (item.Kind == AstValueKind.String && item.Str is not null) result.Add(item.Str);
            else _diags.Error("E003", $"'{a.Key}' entries must be strings.", item.Line, item.Column);
        }
        return result;
    }

    private CountSpec? ExpectCountSpec(AssignmentNode a)
    {
        switch (a.Value)
        {
            case { Kind: AstValueKind.Integer, Int: int n }:
                if (n < 0) { _diags.Error("E003", $"'{a.Key}' cannot be negative.", a.Line, a.Column); return null; }
                return CountSpec.Of(n);

            case { Kind: AstValueKind.FuncCall, FuncName: "random" } rc:
            {
                if (rc.Args.Count != 2 || rc.Args[0].Value.Int is not int min || rc.Args[1].Value.Int is not int max)
                {
                    _diags.Error("E005", "random(min, max) requires exactly two integer arguments.", a.Line, a.Column);
                    return null;
                }
                if (min > max)
                {
                    _diags.Error("E005", $"random({min}, {max}): min must be ≤ max.", a.Line, a.Column);
                    return null;
                }
                return CountSpec.RandomOf(min, max);
            }

            case { Kind: AstValueKind.FuncCall, FuncName: "weighted" } wc:
            {
                if (wc.Args.Count != 1 || wc.Args[0].Value.Kind != AstValueKind.List)
                {
                    _diags.Error("E005", "weighted(...) requires a single list of value: percent pairs.", a.Line, a.Column);
                    return null;
                }
                var weights = new List<(int, int)>();
                foreach (var item in wc.Args[0].Value.Items)
                {
                    if (item is { Kind: AstValueKind.Pair } && item.Items.Count == 2 &&
                        item.Items[0].Int is int v && item.Items[1] is { Kind: AstValueKind.Percent, Int: int p })
                        weights.Add((v, p));
                    else
                        _diags.Error("E005", "weighted entries must be 'value: percent' pairs, e.g. 0: 30%.", item.Line, item.Column);
                }
                int sum = weights.Sum(w => w.Item2);
                if (weights.Count > 0 && sum != 100)
                {
                    _diags.Error("E005", $"weighted percents must sum to 100 (currently {sum}).", a.Line, a.Column);
                    return null;
                }
                return weights.Count > 0 ? CountSpec.WeightedOf(weights) : null;
            }

            default:
                _diags.Error("E003", $"'{a.Key}' must be an integer, random(a,b) or weighted([...]).", a.Line, a.Column);
                return null;
        }
    }
}
