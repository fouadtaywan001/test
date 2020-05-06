using System.Collections.ObjectModel;
using Pusher.App.Mvvm;
using Pusher.Core.Abstractions;
using Pusher.Core.Models;
using Pusher.Core.Planning;

namespace Pusher.App.ViewModels;

/// <summary>One missed project summary in the reschedule dialog.</summary>
public sealed record MissedProjectRow(string Name, int MissedCount, string FirstMissedLocal);

/// <summary>One upcoming push in the cross-project timeline.</summary>
public sealed record TimelineRow(string WhenLocal, string ProjectName, string OrderLabel, string Message);

/// <summary>
/// Backs the "missed pushes" popup. Shown when at least one active project's NEXT pending
/// commit (the head — strict order means only the head matters) was missed by more than
/// 30 minutes. Offers the two logical repairs, applied to every affected project:
///
/// - Catch up now: head runs immediately, later missed commits spread forward with
///   randomized realistic gaps; future commits move only if they'd collide.
/// - Shift forward: the whole pending tail moves by whole days, keeping each commit's
///   time-of-day and the original day pattern.
///
/// Below the choice, the dialog shows the resulting timeline across ALL projects so the
/// user always sees what the schedule will look like.
/// </summary>
public sealed class RescheduleViewModel : ObservableObject
{
    private static readonly TimeSpan MissedThreshold = TimeSpan.FromMinutes(30);

    private readonly IStateStore _store;
    private string _statusMessage = "";

    public ObservableCollection<MissedProjectRow> MissedProjects { get; } = [];
    public ObservableCollection<TimelineRow> Timeline { get; } = [];
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool HasMissed => MissedProjects.Count > 0;

    public RelayCommand CatchUpNowCommand { get; }
    public RelayCommand ShiftForwardCommand { get; }

    /// <summary>Set by the window: closes the dialog with success after applying a fix.</summary>
    public Action? CloseWithSuccess { get; set; }

    public RescheduleViewModel(IStateStore store)
    {
        _store = store;
        CatchUpNowCommand = new RelayCommand(() => Apply(catchUp: true), () => HasMissed);
        ShiftForwardCommand = new RelayCommand(() => Apply(catchUp: false), () => HasMissed);
        Reload();
    }

    /// <summary>True when any active project has a missed head commit. Static helper so the
    /// dashboard can decide whether to pop the dialog without building the whole VM.</summary>
    public static bool AnyMissed(IStateStore store)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        foreach (var project in store.GetAllProjects())
        {
            if (project.Status != ProjectStatus.Active) continue;
            var head = PendingInOrder(store, project.Id).FirstOrDefault();
            if (head is not null && head.ScheduledAtUtc < nowUtc - MissedThreshold) return true;
        }
        return false;
    }

    private static List<SlotTime> PendingInOrder(IStateStore store, long projectId)
    {
        var slots = store.GetSlots(projectId).ToDictionary(s => s.CommitPlanId);
        return store.GetCommitPlans(projectId)
            .Where(p => p.Status == CommitPlanStatus.Pending && slots.ContainsKey(p.Id))
            .OrderBy(p => p.Order)
            .Select(p => new SlotTime(p.Id, slots[p.Id].ScheduledAtUtc))
            .ToList();
    }

    private void Reload()
    {
        MissedProjects.Clear();
        Timeline.Clear();
        var nowUtc = DateTimeOffset.UtcNow;
        var upcoming = new List<(DateTimeOffset At, TimelineRow Row)>();

        foreach (var project in _store.GetAllProjects())
        {
            if (project.Status != ProjectStatus.Active) continue;

            var slots = _store.GetSlots(project.Id).ToDictionary(s => s.CommitPlanId);
            var pendingPlans = _store.GetCommitPlans(project.Id)
                .Where(p => p.Status == CommitPlanStatus.Pending && slots.ContainsKey(p.Id))
                .OrderBy(p => p.Order)
                .ToList();
            if (pendingPlans.Count == 0) continue;

            var headTime = slots[pendingPlans[0].Id].ScheduledAtUtc;
            if (headTime < nowUtc - MissedThreshold)
            {
                var missed = pendingPlans.Count(p => slots[p.Id].ScheduledAtUtc < nowUtc);
                MissedProjects.Add(new MissedProjectRow(
                    project.Name, missed,
                    headTime.ToLocalTime().ToString("ddd d MMM HH:mm")));
            }

            foreach (var p in pendingPlans)
            {
                var at = slots[p.Id].ScheduledAtUtc;
                upcoming.Add((at, new TimelineRow(
                    at.ToLocalTime().ToString("ddd d MMM HH:mm"),
                    project.Name, p.Order.ToString("00"), p.Message)));
            }
        }

        foreach (var (_, row) in upcoming.OrderBy(x => x.At).Take(30))
            Timeline.Add(row);

        OnPropertyChanged(nameof(HasMissed));
    }

    private void Apply(bool catchUp)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var repaired = 0;

        foreach (var project in _store.GetAllProjects())
        {
            if (project.Status != ProjectStatus.Active) continue;

            var pending = PendingInOrder(_store, project.Id);
            if (pending.Count == 0 || pending[0].ScheduledAtUtc >= nowUtc - MissedThreshold) continue;

            var retimed = catchUp
                ? Rescheduler.CatchUpFromNow(pending, nowUtc, seed: unchecked(project.ScriptSeed + pending.Count))
                : Rescheduler.ShiftForwardByDays(pending, nowUtc);

            foreach (var st in retimed)
                _store.UpdatePendingSlotSchedule(st.PlanId, st.ScheduledAtUtc);
            repaired++;
        }

        Reload();
        StatusMessage = catchUp
            ? $"Rescheduled {repaired} project(s): catching up starting now with realistic gaps."
            : $"Rescheduled {repaired} project(s): schedule shifted forward, time-of-day pattern kept.";
        CloseWithSuccess?.Invoke();
    }
}
