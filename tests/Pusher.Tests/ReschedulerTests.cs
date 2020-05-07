using Pusher.Core.Planning;
using Xunit;

namespace Pusher.Tests;

public class ReschedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static List<SlotTime> Pending(params DateTimeOffset[] times)
        => times.Select((t, i) => new SlotTime(i + 1, t)).ToList();

    [Fact]
    public void CatchUp_FirstMissedRunsNow_RestSpreadWithRealisticGaps()
    {
        // 3 missed (hours ago) + 2 future
        var pending = Pending(
            Now.AddHours(-5), Now.AddHours(-4), Now.AddHours(-3),
            Now.AddHours(2), Now.AddHours(5));

        var result = Rescheduler.CatchUpFromNow(pending, Now, seed: 42);

        Assert.Equal(5, result.Count);
        Assert.Equal(Now, result[0].ScheduledAtUtc);                 // head runs immediately
        for (int i = 1; i < 3; i++)                                  // missed tail spreads forward
        {
            var gap = result[i].ScheduledAtUtc - result[i - 1].ScheduledAtUtc;
            Assert.InRange(gap.TotalMinutes, Rescheduler.DefaultMinGapMinutes, Rescheduler.DefaultMaxGapMinutes);
        }
        Assert.Equal(Now.AddHours(2), result[3].ScheduledAtUtc);     // future slots untouched
        Assert.Equal(Now.AddHours(5), result[4].ScheduledAtUtc);
    }

    [Fact]
    public void CatchUp_TimesAlwaysStrictlyAscending_EvenWithManyMissed()
    {
        // 40 missed commits: enough retiming to overrun a "future" slot 30 minutes out.
        var times = Enumerable.Range(0, 40).Select(i => Now.AddDays(-10).AddHours(i)).ToList();
        times.Add(Now.AddMinutes(30)); // near-future slot that MUST be pushed out
        var pending = times.Select((t, i) => new SlotTime(i + 1, t)).ToList();

        var result = Rescheduler.CatchUpFromNow(pending, Now, seed: 7);

        for (int i = 1; i < result.Count; i++)
            Assert.True(result[i].ScheduledAtUtc > result[i - 1].ScheduledAtUtc,
                $"slot {i} not after slot {i - 1}");
    }

    [Fact]
    public void CatchUp_NeverBursts_MinGapRespectedBetweenMissed()
    {
        var pending = Pending(Enumerable.Range(0, 10).Select(i => Now.AddHours(-20 + i)).ToArray());
        var result = Rescheduler.CatchUpFromNow(pending, Now, seed: 1);

        for (int i = 1; i < result.Count; i++)
        {
            var gap = result[i].ScheduledAtUtc - result[i - 1].ScheduledAtUtc;
            Assert.True(gap.TotalMinutes >= Rescheduler.DefaultMinGapMinutes,
                $"gap between {i - 1} and {i} is only {gap.TotalMinutes} min — a burst");
        }
    }

    [Fact]
    public void CatchUp_Deterministic_SameSeedSameResult()
    {
        var pending = Pending(Now.AddHours(-3), Now.AddHours(-2), Now.AddHours(4));
        var a = Rescheduler.CatchUpFromNow(pending, Now, seed: 99);
        var b = Rescheduler.CatchUpFromNow(pending, Now, seed: 99);
        Assert.Equal(a.Select(x => x.ScheduledAtUtc), b.Select(x => x.ScheduledAtUtc));
    }

    [Fact]
    public void ShiftForward_KeepsTimeOfDayAndOrder()
    {
        // first slot was due 2.5 days ago at 09:30; pattern must survive
        var pending = Pending(
            Now.AddDays(-2).AddHours(-2.5),
            Now.AddDays(-2).AddHours(1),
            Now.AddDays(-1),
            Now.AddDays(3));

        var result = Rescheduler.ShiftForwardByDays(pending, Now);

        // all shifted by the SAME whole number of days
        var shift = result[0].ScheduledAtUtc - pending[0].ScheduledAtUtc;
        Assert.Equal(0, shift.TotalDays % 1, 10);
        Assert.All(result.Zip(pending), pair =>
            Assert.Equal(shift, pair.First.ScheduledAtUtc - pair.Second.ScheduledAtUtc));

        // first slot is now in the future, order preserved
        Assert.True(result[0].ScheduledAtUtc > Now);
        for (int i = 1; i < result.Count; i++)
            Assert.True(result[i].ScheduledAtUtc > result[i - 1].ScheduledAtUtc);
    }

    [Fact]
    public void ShiftForward_EmptyPending_ReturnsEmpty()
        => Assert.Empty(Rescheduler.ShiftForwardByDays([], Now));
}
