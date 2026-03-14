using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Administrative management API for durable reminders.
/// </summary>
public sealed class ReminderManagementGrain(IReminderTable reminderTable) : Grain, IReminderManagementGrain
{
    private readonly IReminderTable _reminderTable = reminderTable;

    public Task<ReminderManagementPage> ListAllAsync(int pageSize = 256, string? continuationToken = null)
        => ListFilteredAsync(new ReminderQueryFilter(), pageSize, continuationToken);

    public Task<ReminderManagementPage> ListOverdueAsync(TimeSpan overdueBy, int pageSize = 256, string? continuationToken = null)
        => ListFilteredAsync(
            new ReminderQueryFilter
            {
                Status = ReminderQueryStatus.Overdue,
                OverdueBy = overdueBy,
            },
            pageSize,
            continuationToken);

    public Task<ReminderManagementPage> ListDueInRangeAsync(
        DateTime fromUtcInclusive,
        DateTime toUtcInclusive,
        int pageSize = 256,
        string? continuationToken = null)
        => ListFilteredAsync(
            new ReminderQueryFilter
            {
                DueFromUtcInclusive = fromUtcInclusive,
                DueToUtcInclusive = toUtcInclusive,
            },
            pageSize,
            continuationToken);

    public async Task<ReminderManagementPage> ListFilteredAsync(ReminderQueryFilter filter, int pageSize = 256, string? continuationToken = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var pageIndex = string.IsNullOrWhiteSpace(continuationToken)
            ? 0
            : int.Parse(continuationToken, CultureInfo.InvariantCulture);

        var all = await GetAllAsync();
        var filtered = all.Where(reminder => MatchesFilter(reminder, filter)).OrderBy(reminder => reminder, ReminderEntryComparer.Instance).ToList();
        var page = filtered.Skip(pageIndex * pageSize).Take(pageSize).ToList();
        var hasMore = (pageIndex + 1) * pageSize < filtered.Count;

        return new ReminderManagementPage
        {
            Reminders = page,
            ContinuationToken = hasMore ? (pageIndex + 1).ToString(CultureInfo.InvariantCulture) : null,
        };
    }

    public async Task<IEnumerable<ReminderEntry>> UpcomingAsync(TimeSpan horizon)
    {
        if (horizon < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(horizon));
        }

        var upper = DateTime.UtcNow.Add(horizon);
        return (await GetAllAsync()).Where(reminder => (reminder.NextDueUtc ?? reminder.StartAt) <= upper).OrderBy(reminder => reminder, ReminderEntryComparer.Instance).ToList();
    }

    public async Task<IEnumerable<ReminderEntry>> ListForGrainAsync(GrainId grainId)
        => (await _reminderTable.ReadRows(grainId)).Reminders.OrderBy(reminder => reminder, ReminderEntryComparer.Instance).ToList();

    public async Task<int> CountAllAsync() => (await GetAllAsync()).Count;

    public async Task SetPriorityAsync(GrainId grainId, string name, Runtime.ReminderPriority priority)
    {
        var entry = await GetEntryAsync(grainId, name);
        entry.Priority = priority;
        entry.ETag = await _reminderTable.UpsertRow(entry);
    }

    public async Task SetActionAsync(GrainId grainId, string name, Runtime.MissedReminderAction action)
    {
        var entry = await GetEntryAsync(grainId, name);
        entry.Action = action;
        entry.ETag = await _reminderTable.UpsertRow(entry);
    }

    public async Task RepairAsync(GrainId grainId, string name)
    {
        var entry = await GetEntryAsync(grainId, name);
        entry.NextDueUtc = CalculateNextDue(entry, DateTime.UtcNow);
        entry.ETag = await _reminderTable.UpsertRow(entry);
    }

    public async Task DeleteAsync(GrainId grainId, string name)
    {
        var entry = await GetEntryAsync(grainId, name);
        await _reminderTable.RemoveRow(grainId, name, entry.ETag);
    }

    private async Task<List<ReminderEntry>> GetAllAsync()
        => (await _reminderTable.ReadRows(0, 0)).Reminders.ToList();

    private async Task<ReminderEntry> GetEntryAsync(GrainId grainId, string name)
        => await _reminderTable.ReadRow(grainId, name) ?? throw new Runtime.ReminderException($"Reminder '{name}' for grain '{grainId}' was not found.");

    private static bool MatchesFilter(ReminderEntry reminder, ReminderQueryFilter filter)
    {
        var due = reminder.NextDueUtc ?? reminder.StartAt;
        var now = DateTime.UtcNow;

        if (filter.DueFromUtcInclusive is { } from && due < from)
        {
            return false;
        }

        if (filter.DueToUtcInclusive is { } to && due > to)
        {
            return false;
        }

        if (filter.Priority is { } priority && reminder.Priority != priority)
        {
            return false;
        }

        if (filter.Action is { } action && reminder.Action != action)
        {
            return false;
        }

        if (filter.ScheduleKind is { } scheduleKind && GetScheduleKind(reminder) != scheduleKind)
        {
            return false;
        }

        if (filter.Status == ReminderQueryStatus.Any)
        {
            return true;
        }

        var matched = false;
        if ((filter.Status & ReminderQueryStatus.Due) != 0 && due <= now)
        {
            matched = true;
        }

        if ((filter.Status & ReminderQueryStatus.Upcoming) != 0 && due > now)
        {
            matched = true;
        }

        if ((filter.Status & ReminderQueryStatus.Overdue) != 0 && due <= now - filter.OverdueBy)
        {
            matched = true;
        }

        if ((filter.Status & ReminderQueryStatus.Missed) != 0
            && due <= now - filter.MissedBy
            && (reminder.LastFireUtc is null || reminder.LastFireUtc < due))
        {
            matched = true;
        }

        return matched;
    }

    private static DateTime? CalculateNextDue(ReminderEntry entry, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(entry.CronExpression))
        {
            return ReminderCronSchedule.Parse(entry.CronExpression, entry.CronTimeZoneId).GetNextOccurrence(now);
        }

        if (entry.Period <= TimeSpan.Zero)
        {
            return null;
        }

        var next = entry.NextDueUtc ?? entry.StartAt;
        if (next <= now)
        {
            var ticksBehind = now.Ticks - next.Ticks;
            var periodsBehind = ticksBehind / entry.Period.Ticks + 1;
            next = next.AddTicks(periodsBehind * entry.Period.Ticks);
        }

        return next;
    }

    private static Runtime.ReminderScheduleKind GetScheduleKind(ReminderEntry reminder)
        => string.IsNullOrWhiteSpace(reminder.CronExpression)
            ? Runtime.ReminderScheduleKind.Interval
            : Runtime.ReminderScheduleKind.Cron;

    private sealed class ReminderEntryComparer : IComparer<ReminderEntry>
    {
        public static ReminderEntryComparer Instance { get; } = new();

        public int Compare(ReminderEntry? x, ReminderEntry? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var dueCompare = (x.NextDueUtc ?? x.StartAt).CompareTo(y.NextDueUtc ?? y.StartAt);
            if (dueCompare != 0)
            {
                return dueCompare;
            }

            var grainCompare = x.GrainId.CompareTo(y.GrainId);
            if (grainCompare != 0)
            {
                return grainCompare;
            }

            return string.CompareOrdinal(x.ReminderName, y.ReminderName);
        }
    }
}
