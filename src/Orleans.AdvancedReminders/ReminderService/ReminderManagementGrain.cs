using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Administrative management API for advanced reminders.
/// </summary>
public sealed class ReminderManagementGrain(IReminderTable reminderTable) : Grain, IReminderManagementGrain
{
    private readonly IReminderTable _reminderTable = reminderTable;
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    internal ReminderManagementGrain(IReminderTable reminderTable, IServiceProvider? serviceProvider, TimeProvider? timeProvider = null)
        : this(reminderTable)
    {
        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private readonly IServiceProvider? _serviceProvider;

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

        var cursor = ReminderCursor.Parse(continuationToken);
        var now = GetUtcNow();
        var candidates = await SelectPageAsync(filter, cursor, pageSize + 1, now);
        var hasMore = candidates.Count > pageSize;
        if (hasMore)
        {
            candidates.RemoveRange(pageSize, candidates.Count - pageSize);
        }

        return new ReminderManagementPage
        {
            Reminders = candidates,
            ContinuationToken = hasMore && candidates.Count > 0 ? ReminderCursor.Create(candidates[^1]) : null,
        };
    }

    public async Task<IEnumerable<ReminderEntry>> UpcomingAsync(TimeSpan horizon)
    {
        if (horizon < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(horizon));
        }

        var upper = GetUtcNow().Add(horizon);
        return (await GetAllAsync())
            .Where(reminder => GetDueTime(reminder) <= upper)
            .OrderBy(reminder => reminder, ReminderEntryComparer.Instance)
            .ToList();
    }

    public async Task<IEnumerable<ReminderEntry>> ListForGrainAsync(GrainId grainId)
        => (await _reminderTable.ReadRows(grainId)).Reminders.OrderBy(reminder => reminder, ReminderEntryComparer.Instance).ToList();

    public async Task<int> CountAllAsync() => (await _reminderTable.ReadRows(0, 0)).Reminders.Count;

    public async Task SetPriorityAsync(GrainId grainId, string name, Runtime.ReminderPriority priority)
    {
        var entry = await GetEntryAsync(grainId, name);
        entry.Priority = priority;
        await PersistMutationAsync(entry);
    }

    public async Task SetActionAsync(GrainId grainId, string name, Runtime.MissedReminderAction action)
    {
        var entry = await GetEntryAsync(grainId, name);
        entry.Action = action;
        await PersistMutationAsync(entry);
    }

    public async Task RepairAsync(GrainId grainId, string name)
    {
        var entry = await GetEntryAsync(grainId, name);
        entry.NextDueUtc = CalculateNextDue(entry, GetUtcNow());
        await PersistMutationAsync(entry);
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

    private async Task PersistMutationAsync(ReminderEntry entry)
    {
        if (entry.NextDueUtc is null)
        {
            entry.ETag = await _reminderTable.UpsertRow(entry);
            return;
        }

        var serviceProvider = _serviceProvider ?? ServiceProvider;
        var reminderService = serviceProvider?.GetService(typeof(Runtime.ReminderService.AdvancedReminderService)) as Runtime.ReminderService.AdvancedReminderService;
        if (reminderService is null)
        {
            entry.ETag = await _reminderTable.UpsertRow(entry);
            return;
        }

        await reminderService.UpsertAndScheduleEntryAsync(entry, CancellationToken.None);
    }

    private async Task<List<ReminderEntry>> SelectPageAsync(ReminderQueryFilter filter, ReminderCursor? cursor, int take, DateTime now)
    {
        var queue = new PriorityQueue<ReminderEntry, ReminderEntry>(ReverseReminderEntryComparer.Instance);
        foreach (var reminder in await GetAllAsync())
        {
            if (!MatchesFilter(reminder, filter, now) || !IsAfterCursor(reminder, cursor))
            {
                continue;
            }

            queue.Enqueue(reminder, reminder);
            if (queue.Count > take)
            {
                _ = queue.Dequeue();
            }
        }

        var result = new List<ReminderEntry>(queue.Count);
        while (queue.Count > 0)
        {
            result.Add(queue.Dequeue());
        }

        result.Sort(ReminderEntryComparer.Instance);
        return result;
    }

    private bool MatchesFilter(ReminderEntry reminder, ReminderQueryFilter filter, DateTime now)
    {
        var due = GetDueTime(reminder);

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

    private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static bool IsAfterCursor(ReminderEntry reminder, ReminderCursor? cursor)
        => cursor is null || ReminderCursor.Compare(reminder, cursor) > 0;

    private static DateTime GetDueTime(ReminderEntry reminder) => reminder.NextDueUtc ?? reminder.StartAt;

    private static Runtime.ReminderScheduleKind GetScheduleKind(ReminderEntry reminder)
        => string.IsNullOrWhiteSpace(reminder.CronExpression)
            ? Runtime.ReminderScheduleKind.Interval
            : Runtime.ReminderScheduleKind.Cron;

    private sealed class ReminderCursor
    {
        private ReminderCursor(DateTime dueUtc, GrainId grainId, string reminderName)
        {
            DueUtc = dueUtc;
            GrainId = grainId;
            ReminderName = reminderName;
        }

        public DateTime DueUtc { get; }

        public GrainId GrainId { get; }

        public string ReminderName { get; }

        public static string Create(ReminderEntry entry)
        {
            var payload = string.Create(
                CultureInfo.InvariantCulture,
                $"{GetDueTime(entry).Ticks}\n{entry.GrainId}\n{entry.ReminderName}");
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        }

        public static ReminderCursor? Parse(string? continuationToken)
        {
            if (string.IsNullOrWhiteSpace(continuationToken))
            {
                return null;
            }

            try
            {
                var payload = Encoding.UTF8.GetString(Convert.FromBase64String(continuationToken));
                var firstSeparator = payload.IndexOf('\n');
                var secondSeparator = firstSeparator >= 0 ? payload.IndexOf('\n', firstSeparator + 1) : -1;
                if (firstSeparator <= 0 || secondSeparator <= firstSeparator + 1 || secondSeparator >= payload.Length - 1)
                {
                    throw new FormatException("Continuation token payload is incomplete.");
                }

                var ticksSpan = payload.AsSpan(0, firstSeparator);
                if (!long.TryParse(ticksSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dueTicks))
                {
                    throw new FormatException("Continuation token due timestamp is invalid.");
                }

                var grainIdText = payload.Substring(firstSeparator + 1, secondSeparator - firstSeparator - 1);
                var reminderName = payload[(secondSeparator + 1)..];
                return new ReminderCursor(new DateTime(dueTicks, DateTimeKind.Utc), GrainId.Parse(grainIdText), reminderName);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new ArgumentException("Invalid continuation token.", nameof(continuationToken), exception);
            }
        }

        public static int Compare(ReminderEntry reminder, ReminderCursor cursor)
        {
            var dueCompare = GetDueTime(reminder).CompareTo(cursor.DueUtc);
            if (dueCompare != 0)
            {
                return dueCompare;
            }

            var grainCompare = reminder.GrainId.CompareTo(cursor.GrainId);
            if (grainCompare != 0)
            {
                return grainCompare;
            }

            return string.CompareOrdinal(reminder.ReminderName, cursor.ReminderName);
        }
    }

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

            var dueCompare = GetDueTime(x).CompareTo(GetDueTime(y));
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

    private sealed class ReverseReminderEntryComparer : IComparer<ReminderEntry>
    {
        public static ReverseReminderEntryComparer Instance { get; } = new();

        public int Compare(ReminderEntry? x, ReminderEntry? y) => ReminderEntryComparer.Instance.Compare(y, x);
    }
}
