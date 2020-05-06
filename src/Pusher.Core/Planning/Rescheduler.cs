namespace Pusher.Core.Planning;

/// <summary>A pending slot's identity and scheduled time, in commit order.</summary>
public sealed record SlotTime(long PlanId, DateTimeOffset ScheduledAtUtc);

/// <summary>
/// Keeps a project's pending schedule logical after downtime. Two strategies:
///
/// - <see cref="CatchUpFromNow"/>: the FIRST missed commit runs now, later missed ones are
///   spread forward with randomized realistic gaps (never a burst), and future slots are
///   pushed just far enough to stay strictly after the retimed tail.
/// - <see cref="ShiftForwardByDays"/>: the whole pending tail moves forward by whole days,
///   preserving each slot's time-of-day and the original day-to-day pattern.
///
/// Both guarantee strictly ascending times with a minimum gap, so commit order N can
/// never be scheduled at or before commit N-1. Pure functions — unit-testable, shared
/// by the worker (automatic policy) and the UI reschedule dialog (manual choice).
/// </summary>
public static class Rescheduler
{
    /// <summary>Default spacing bounds (minutes) between catch-up pushes.</summary>
    public const int DefaultMinGapMinutes = 12;
    public const int DefaultMaxGapMinutes = 35;

    public static List<SlotTime> CatchUpFromNow(
        IReadOnlyList<SlotTime> pendingInOrder, DateTimeOffset nowUtc, int seed,
        int minGapMinutes = DefaultMinGapMinutes, int maxGapMinutes = DefaultMaxGapMinutes)
    {
        if (minGapMinutes < 1) minGapMinutes = 1;
        if (maxGapMinutes < minGapMinutes) maxGapMinutes = minGapMinutes;

        var rng = new Random(seed);
        var result = new List<SlotTime>(pendingInOrder.Count);
        var lastAssigned = DateTimeOffset.MinValue;
        var cursor = nowUtc;

        foreach (var slot in pendingInOrder)
        {
            DateTimeOffset assigned;
            if (slot.ScheduledAtUtc <= nowUtc)
            {
                // Missed (or due right now): first one runs immediately, the rest follow
                // at randomized gaps so the catch-up looks like a human work session.
                assigned = cursor;
                cursor = assigned + TimeSpan.FromMinutes(rng.Next(minGapMinutes, maxGapMinutes + 1));
            }
            else
            {
                // Future slot: keep its planned time unless it collides with the retimed tail.
                assigned = slot.ScheduledAtUtc;
                var minAllowed = lastAssigned + TimeSpan.FromMinutes(minGapMinutes);
                if (assigned < minAllowed)
                    assigned = minAllowed + TimeSpan.FromMinutes(rng.Next(0, 11));
            }

            result.Add(new SlotTime(slot.PlanId, assigned));
            lastAssigned = assigned;
        }

        return result;
    }

    public static List<SlotTime> ShiftForwardByDays(
        IReadOnlyList<SlotTime> pendingInOrder, DateTimeOffset nowUtc, int graceMinutes = 10)
    {
        if (pendingInOrder.Count == 0) return [];

        // Smallest whole-day shift that puts the FIRST pending slot in the future.
        var first = pendingInOrder[0].ScheduledAtUtc;
        int days = 0;
        while (first.AddDays(days) <= nowUtc.AddMinutes(graceMinutes))
            days++;

        return pendingInOrder
            .Select(s => new SlotTime(s.PlanId, s.ScheduledAtUtc.AddDays(days)))
            .ToList();
    }
}
