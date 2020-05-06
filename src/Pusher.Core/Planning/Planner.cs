using Pusher.Core.Models;
using Pusher.Core.Scripting;
using Pusher.Core.Splitting;

namespace Pusher.Core.Planning;

/// <summary>One fully-planned commit: what, when, and in which mode.</summary>
public sealed record PlannedCommit(
    int Order,
    string Message,
    string Body,
    IReadOnlyList<string> Globs,
    IReadOnlyList<string> Files,
    CommitMode Mode,
    DateTimeOffset ScheduledAt);   // local time with correct offset; store .ToUniversalTime()

public sealed record PlanResult(IReadOnlyList<PlannedCommit> Commits, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Success => Diagnostics.All(d => !d.IsError);
}

/// <summary>
/// Turns a validated ScriptModel + resolved commits into a concrete calendar.
/// Deterministic: (script, file list, seed) always yields the identical plan
/// (plan/05-pushscript-dsl.md §9). Schedule math runs in local time; DST-invalid
/// times shift forward, ambiguous times take the first occurrence (§6.3).
/// Commit dates ascend monotonically across backdate + forward blocks.
/// </summary>
public sealed class Planner
{
    private readonly TimeZoneInfo _tz;
    private readonly DateOnly _today;

    public Planner(TimeZoneInfo? timeZone = null, DateOnly? today = null)
    {
        _tz = timeZone ?? TimeZoneInfo.Local;
        _today = today ?? DateOnly.FromDateTime(DateTime.Now);
    }

    public PlanResult Plan(ScriptModel model, IReadOnlyList<string> projectFiles, int seed)
    {
        var diags = new DiagnosticBag();
        var resolved = SplitResolver.Resolve(model.Split, projectFiles, diags);
        if (diags.HasErrors)
            return new PlanResult([], diags.Items);

        var planned = new List<PlannedCommit>();
        int backdateCount = model.Backdate is null ? 0
            : model.Backdate.CommitCount ?? resolved.Count;

        DateTimeOffset? lastAssigned = null;

        // ---- backdated batch (first N manifest commits) --------------------
        if (model.Backdate is not null && backdateCount > 0)
        {
            var backdated = resolved.Take(backdateCount).ToList();
            var times = GenerateWindowTimes(
                from: model.Backdate.From,
                to: model.Backdate.To,
                count: backdated.Count,
                perDay: model.Backdate.PerDay,
                activeFrom: model.Backdate.ActiveFrom,
                activeTo: model.Backdate.ActiveTo,
                jitterMinutes: 0,
                restDaysPercent: model.Backdate.RestDaysPercent,
                minGapMinutes: model.Backdate.MinGapMinutes,
                dayRules: [],
                seed: seed,
                hardEnd: true,
                diags: diags);

            if (times.Count < backdated.Count)
            {
                diags.Error("V-BACK-1",
                    $"The backdate window {model.Backdate.From:yyyy-MM-dd}..{model.Backdate.To:yyyy-MM-dd} cannot hold {backdated.Count} commits with the given per_day/rest_days. Widen the window or raise per_day.",
                    1, 1);
                return new PlanResult([], diags.Items);
            }

            for (int i = 0; i < backdated.Count; i++)
            {
                var rc = backdated[i];
                planned.Add(new PlannedCommit(rc.Order, rc.Message, rc.Body, rc.Globs, rc.Files, CommitMode.Backdated, times[i]));
                lastAssigned = times[i];
            }
        }

        // ---- forward schedule ----------------------------------------------
        var forward = resolved.Skip(backdateCount).ToList();
        if (forward.Count > 0)
        {
            if (model.Schedule is null)
            {
                diags.Error("V-BACK-2", $"{forward.Count} commits remain after the backdate block but there is no 'schedule' block.", 1, 1);
                return new PlanResult([], diags.Items);
            }

            var start = model.Schedule.StartIsToday ? _today : model.Schedule.Start!.Value;
            // never earlier than the last backdated commit (monotonic dates)
            if (lastAssigned is not null && DateOnly.FromDateTime(lastAssigned.Value.LocalDateTime) >= start)
                start = DateOnly.FromDateTime(lastAssigned.Value.LocalDateTime).AddDays(1);

            var times = GenerateWindowTimes(
                from: start,
                to: model.Schedule.End,
                count: forward.Count,
                perDay: model.Schedule.PerDay,
                activeFrom: model.Schedule.ActiveFrom,
                activeTo: model.Schedule.ActiveTo,
                jitterMinutes: model.Schedule.JitterMinutes,
                restDaysPercent: model.Schedule.RestDaysPercent,
                minGapMinutes: model.Schedule.MinGapMinutes,
                dayRules: model.Schedule.DayRules,
                seed: seed,
                hardEnd: model.Schedule.End is not null,
                diags: diags);

            if (times.Count < forward.Count)
            {
                diags.Error("V-SCHED-3",
                    $"Could not place all {forward.Count} forward commits before schedule.end. Extend 'end', raise 'per_day', or reduce 'rest_days'.",
                    1, 1);
                return new PlanResult([], diags.Items);
            }

            for (int i = 0; i < forward.Count; i++)
            {
                var rc = forward[i];
                planned.Add(new PlannedCommit(rc.Order, rc.Message, rc.Body, rc.Globs, rc.Files, CommitMode.Forward, times[i]));
            }
        }

        return new PlanResult(planned, diags.Items);
    }

    /// <summary>
    /// Walks days from <paramref name="from"/>, rolling per-day counts and slot times
    /// from day-derived RNGs (insertion-order independent), until <paramref name="count"/>
    /// slots exist or the hard end / safety horizon is hit. Times ascend strictly.
    /// </summary>
    private List<DateTimeOffset> GenerateWindowTimes(
        DateOnly from, DateOnly? to, int count, CountSpec perDay,
        TimeOnly activeFrom, TimeOnly activeTo, int jitterMinutes,
        int restDaysPercent, int minGapMinutes,
        IReadOnlyList<DayRule> dayRules, int seed, bool hardEnd, DiagnosticBag diags)
    {
        var result = new List<DateTimeOffset>(count);
        var day = from;
        var safetyHorizon = from.AddDays(365 * 3); // absolute upper bound for open-ended schedules

        while (result.Count < count)
        {
            if (to is not null && day > to)
            {
                if (hardEnd) break;
            }
            if (day > safetyHorizon) break;

            // effective settings for this day (later rules win; skip zeroes the day)
            var effPerDay = perDay;
            var effFrom = activeFrom;
            var effTo = activeTo;
            int effJitter = jitterMinutes;
            int effRest = restDaysPercent;
            int effGap = minGapMinutes;
            bool skip = false;

            foreach (var rule in dayRules)
            {
                if (!rule.Matches(day)) continue;
                if (rule.IsSkip) { skip = true; continue; }
                skip = false;
                if (rule.PerDay is not null) effPerDay = rule.PerDay;
                if (rule.ActiveFrom is not null) effFrom = rule.ActiveFrom.Value;
                if (rule.ActiveTo is not null) effTo = rule.ActiveTo.Value;
                if (rule.JitterMinutes is not null) effJitter = rule.JitterMinutes.Value;
                if (rule.RestDaysPercent is not null) effRest = rule.RestDaysPercent.Value;
                if (rule.MinGapMinutes is not null) effGap = rule.MinGapMinutes.Value;
            }

            if (!skip)
            {
                var rng = DeterministicRandom.ForDay(seed, day);

                if (effRest > 0 && rng.Chance(effRest))
                {
                    day = day.AddDays(1);
                    continue;
                }

                int n = Math.Min(RollCount(effPerDay, rng), count - result.Count);
                if (n > 0)
                {
                    var minutes = PickDayMinutes(rng, effFrom, effTo, n, effGap, effJitter);
                    foreach (var m in minutes)
                        result.Add(ToOffset(day, TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(m))));
                }
            }

            day = day.AddDays(1);
        }

        return result;
    }

    private static int RollCount(CountSpec spec, DeterministicRandom rng) => spec.Kind switch
    {
        CountSpec.SpecKind.Fixed => spec.Fixed,
        CountSpec.SpecKind.Random => rng.NextInt(spec.Min, spec.Max),
        _ => RollWeighted(spec, rng),
    };

    private static int RollWeighted(CountSpec spec, DeterministicRandom rng)
    {
        int roll = rng.NextInt(0, 99);
        int acc = 0;
        foreach (var (value, percent) in spec.Weights)
        {
            acc += percent;
            if (roll < acc) return value;
        }
        return spec.Weights.Count > 0 ? spec.Weights[^1].Value : 0;
    }

    /// <summary>
    /// Picks <paramref name="n"/> strictly-ascending minute offsets inside the window,
    /// honoring min_gap, with per-slot jitter clamped to the window. Deterministic:
    /// segments the window evenly, then jitters within each segment.
    /// </summary>
    private static List<int> PickDayMinutes(DeterministicRandom rng, TimeOnly from, TimeOnly to, int n, int minGap, int jitter)
    {
        int windowStart = (int)from.ToTimeSpan().TotalMinutes;
        int windowLen = (int)(to.ToTimeSpan() - from.ToTimeSpan()).TotalMinutes;
        var result = new List<int>(n);

        // cap n so min_gap always fits
        if (minGap > 0)
            n = Math.Min(n, Math.Max(1, windowLen / Math.Max(1, minGap)));

        int segment = windowLen / n;
        int prev = -1;

        for (int i = 0; i < n; i++)
        {
            int segStart = windowStart + i * segment;
            int baseMinute = segStart + rng.NextInt(0, Math.Max(0, segment - 1));
            if (jitter > 0)
                baseMinute += rng.NextInt(-jitter, jitter);

            // clamp into window and enforce ascending + gap
            baseMinute = Math.Clamp(baseMinute, windowStart, windowStart + windowLen - 1);
            if (prev >= 0 && baseMinute < prev + Math.Max(1, minGap))
                baseMinute = prev + Math.Max(1, minGap);
            if (baseMinute >= windowStart + windowLen)
                break; // window exhausted; day yields fewer slots

            result.Add(baseMinute);
            prev = baseMinute;
        }

        return result;
    }

    /// <summary>
    /// Converts a local wall-clock time to a DateTimeOffset in the planner's zone.
    /// DST-nonexistent times shift forward to the first valid minute; ambiguous
    /// times resolve to the first (earlier-offset) occurrence. (§6.3)
    /// </summary>
    private DateTimeOffset ToOffset(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);

        if (_tz.IsInvalidTime(local))
        {
            // walk forward to the first valid minute
            while (_tz.IsInvalidTime(local))
                local = local.AddMinutes(1);
        }

        TimeSpan offset;
        if (_tz.IsAmbiguousTime(local))
        {
            // first occurrence = the larger (pre-transition, usually DST) offset
            offset = _tz.GetAmbiguousTimeOffsets(local).Max();
        }
        else
        {
            offset = _tz.GetUtcOffset(local);
        }

        return new DateTimeOffset(local, offset);
    }
}
