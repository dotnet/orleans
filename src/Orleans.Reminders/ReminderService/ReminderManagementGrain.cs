using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Orleans.Reminders.Cron.Internal;
using Orleans.Runtime.ReminderService;

#nullable enable
namespace Orleans;

public sealed class ReminderManagementGrain(IReminderTable reminderTable, TimeProvider timeProvider) : Grain, IReminderManagementGrain
{
    private const int ScanSegmentCount = 128;
    private const int DefaultPageSize = 256;
    private const int MaxPageSize = 4_096;
    private static readonly UpcomingReminderComparer UpcomingComparer = new();
    private static readonly ReminderListCandidateComparer ListCandidateComparer = new();
    private static readonly ReverseReminderListCandidateComparer ReverseListCandidateComparer = new();

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public Task<ReminderManagementPage> ListAllAsync(int pageSize = DefaultPageSize, string? continuationToken = null)
    {
        return ListAsync(
            pageSize,
            continuationToken,
            _ => true);
    }

    public Task<ReminderManagementPage> ListOverdueAsync(TimeSpan overdueBy, int pageSize = DefaultPageSize, string? continuationToken = null)
    {
        if (overdueBy < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(overdueBy), "Overdue threshold must be non-negative.");
        }

        var threshold = UtcNow - overdueBy;
        return ListAsync(
            pageSize,
            continuationToken,
            reminder => (reminder.NextDueUtc ?? reminder.StartAt) <= threshold);
    }

    public Task<ReminderManagementPage> ListDueInRangeAsync(
        DateTime fromUtcInclusive,
        DateTime toUtcInclusive,
        int pageSize = DefaultPageSize,
        string? continuationToken = null)
    {
        EnsureUtc(fromUtcInclusive, nameof(fromUtcInclusive));
        EnsureUtc(toUtcInclusive, nameof(toUtcInclusive));
        if (fromUtcInclusive > toUtcInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(fromUtcInclusive), "The range start must be less than or equal to range end.");
        }

        return ListAsync(
            pageSize,
            continuationToken,
            reminder =>
            {
                var due = reminder.NextDueUtc ?? reminder.StartAt;
                return due >= fromUtcInclusive && due <= toUtcInclusive;
            });
    }

    public Task<ReminderManagementPage> ListFilteredAsync(
        ReminderQueryFilter filter,
        int pageSize = DefaultPageSize,
        string? continuationToken = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);

        var now = UtcNow;
        var dueFrom = filter.DueFromUtcInclusive;
        var dueTo = filter.DueToUtcInclusive;
        var priority = filter.Priority;
        var action = filter.Action;
        var scheduleKind = filter.ScheduleKind;
        var status = filter.Status;
        var overdueThreshold = now - filter.OverdueBy;
        var missedThreshold = now - filter.MissedBy;

        return ListAsync(
            pageSize,
            continuationToken,
            reminder =>
            {
                var due = reminder.NextDueUtc ?? reminder.StartAt;

                if (dueFrom is { } from && due < from)
                {
                    return false;
                }

                if (dueTo is { } to && due > to)
                {
                    return false;
                }

                if (priority is { } expectedPriority && reminder.Priority != expectedPriority)
                {
                    return false;
                }

                if (action is { } expectedAction && reminder.Action != expectedAction)
                {
                    return false;
                }

                if (scheduleKind is { } expectedKind && GetScheduleKind(reminder) != expectedKind)
                {
                    return false;
                }

                return MatchesStatus(reminder, due, now, overdueThreshold, missedThreshold, status);
            });
    }

    public async Task<IEnumerable<ReminderEntry>> UpcomingAsync(TimeSpan horizon)
    {
        if (horizon < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(horizon), "Horizon must be non-negative.");
        }

        var now = UtcNow;
        var upper = now.Add(horizon);
        var dueReminders = new List<ReminderEntry>();

        foreach (var range in EnumerateHashScanRanges())
        {
            var tableData = await reminderTable.ReadRows(range.Begin, range.End);
            if (tableData is null || tableData.Reminders.Count == 0)
            {
                continue;
            }

            foreach (var reminder in tableData.Reminders)
            {
                if ((reminder.NextDueUtc ?? reminder.StartAt) <= upper)
                {
                    dueReminders.Add(reminder);
                }
            }
        }

        if (dueReminders.Count == 0)
        {
            return [];
        }

        dueReminders.Sort(UpcomingComparer);
        return dueReminders;
    }

    public async Task<IEnumerable<ReminderEntry>> ListForGrainAsync(GrainId grainId)
    {
        var tableData = await reminderTable.ReadRows(grainId);
        return tableData.Reminders;
    }

    public async Task<int> CountAllAsync()
    {
        var total = 0;
        foreach (var range in EnumerateHashScanRanges())
        {
            var tableData = await reminderTable.ReadRows(range.Begin, range.End);
            if (tableData is null)
            {
                continue;
            }

            total += tableData.Reminders.Count;
        }

        return total;
    }

    public async Task SetPriorityAsync(GrainId grainId, string name, Runtime.ReminderPriority priority)
    {
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Invalid reminder priority.");
        }

        var entry = await reminderTable.ReadRow(grainId, name);
        if (entry is null)
        {
            return;
        }

        entry.Priority = priority;
        entry.ETag = await UpsertRowOrThrow(grainId, name, entry, "set reminder priority");
    }

    public async Task SetActionAsync(GrainId grainId, string name, Runtime.MissedReminderAction action)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Invalid missed reminder action.");
        }

        var entry = await reminderTable.ReadRow(grainId, name);
        if (entry is null)
        {
            return;
        }

        entry.Action = action;
        entry.ETag = await UpsertRowOrThrow(grainId, name, entry, "set reminder action");
    }

    public async Task RepairAsync(GrainId grainId, string name)
    {
        var entry = await reminderTable.ReadRow(grainId, name);
        if (entry is null)
        {
            return;
        }

        var nextDue = CalculateNextDue(entry, UtcNow);
        if (nextDue is null)
        {
            return;
        }

        entry.NextDueUtc = nextDue;
        entry.ETag = await UpsertRowOrThrow(grainId, name, entry, "repair reminder");
    }

    public async Task DeleteAsync(GrainId grainId, string name)
    {
        var entry = await reminderTable.ReadRow(grainId, name);
        if (entry is null)
        {
            return;
        }

        if (!await reminderTable.RemoveRow(grainId, name, entry.ETag))
        {
            throw CreateConcurrencyException("delete reminder", grainId, name);
        }
    }

    private async Task<ReminderManagementPage> ListAsync(int pageSize, string? continuationToken, Func<ReminderEntry, bool> predicate)
    {
        ValidatePageSize(pageSize);

        (string GrainId, string ReminderName)? cursor = null;
        if (!string.IsNullOrWhiteSpace(continuationToken))
        {
            cursor = ParseContinuationToken(continuationToken);
        }

        var selectionLimit = pageSize + 1;
        var candidates = new PriorityQueue<ReminderListCandidate, ReminderListCandidate>(ReverseListCandidateComparer);

        foreach (var range in EnumerateHashScanRanges())
        {
            var tableData = await reminderTable.ReadRows(range.Begin, range.End);
            if (tableData is null || tableData.Reminders.Count == 0)
            {
                continue;
            }

            foreach (var reminder in tableData.Reminders)
            {
                if (!predicate(reminder))
                {
                    continue;
                }

                var grainId = reminder.GrainId.ToString();
                if (cursor is { } value && !IsAfterCursor(grainId, reminder.ReminderName, value))
                {
                    continue;
                }

                AddCandidate(candidates, new ReminderListCandidate(grainId, reminder.ReminderName, reminder), selectionLimit);
            }
        }

        if (candidates.Count == 0)
        {
            return new ReminderManagementPage
            {
                Reminders = [],
                ContinuationToken = null,
            };
        }

        var orderedCandidates = new List<ReminderListCandidate>(candidates.Count);
        foreach (var item in candidates.UnorderedItems)
        {
            orderedCandidates.Add(item.Element);
        }

        orderedCandidates.Sort(ListCandidateComparer);

        var hasMore = orderedCandidates.Count > pageSize;
        var takeCount = hasMore ? pageSize : orderedCandidates.Count;
        var reminders = new List<ReminderEntry>(takeCount);
        for (var i = 0; i < takeCount; i++)
        {
            reminders.Add(orderedCandidates[i].Reminder);
        }

        var nextToken = hasMore
            ? CreateContinuationToken(orderedCandidates[takeCount - 1].GrainId, orderedCandidates[takeCount - 1].ReminderName)
            : null;

        return new ReminderManagementPage { Reminders = reminders, ContinuationToken = nextToken };
    }

    private static void AddCandidate(
        PriorityQueue<ReminderListCandidate, ReminderListCandidate> selected,
        ReminderListCandidate candidate,
        int selectionLimit)
    {
        if (selected.Count < selectionLimit)
        {
            selected.Enqueue(candidate, candidate);
            return;
        }

        var worst = selected.Peek();
        if (ListCandidateComparer.Compare(candidate, worst) < 0)
        {
            selected.Dequeue();
            selected.Enqueue(candidate, candidate);
        }
    }

    private static bool IsAfterCursor(string grainId, string reminderName, (string GrainId, string ReminderName) cursor)
    {
        var grainCompare = string.CompareOrdinal(grainId, cursor.GrainId);
        if (grainCompare != 0)
        {
            return grainCompare > 0;
        }

        return string.CompareOrdinal(reminderName, cursor.ReminderName) > 0;
    }

    private static string CreateContinuationToken(string grainId, string reminderName)
    {
        var payload = string.Concat(
            grainId.Length.ToString(CultureInfo.InvariantCulture),
            ":",
            grainId,
            reminderName);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    private static (string GrainId, string ReminderName) ParseContinuationToken(string continuationToken)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(continuationToken));
            var separator = decoded.IndexOf(':');
            if (separator <= 0)
            {
                throw new ArgumentException("Continuation token is malformed.");
            }

            if (!int.TryParse(decoded.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var grainIdLength) || grainIdLength < 0)
            {
                throw new ArgumentException("Continuation token prefix is invalid.");
            }

            var remainder = decoded.AsSpan(separator + 1);
            if (remainder.Length < grainIdLength)
            {
                throw new ArgumentException("Continuation token payload is invalid.");
            }

            var grainId = remainder[..grainIdLength].ToString();
            var reminderName = remainder[grainIdLength..].ToString();

            if (string.IsNullOrEmpty(grainId))
            {
                throw new ArgumentException("Continuation token grain id is missing.");
            }

            return (grainId, reminderName);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ArgumentException("Invalid continuation token.", nameof(continuationToken), exception);
        }
    }

    private static void ValidatePageSize(int pageSize)
    {
        if (pageSize <= 0 || pageSize > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, $"Page size must be in range [1, {MaxPageSize}].");
        }
    }

    private static void ValidateFilter(ReminderQueryFilter filter)
    {
        if (filter.DueFromUtcInclusive is { } dueFrom)
        {
            EnsureUtc(dueFrom, nameof(filter.DueFromUtcInclusive));
        }

        if (filter.DueToUtcInclusive is { } dueTo)
        {
            EnsureUtc(dueTo, nameof(filter.DueToUtcInclusive));
        }

        if (filter.DueFromUtcInclusive is { } from && filter.DueToUtcInclusive is { } to && from > to)
        {
            throw new ArgumentOutOfRangeException(nameof(filter.DueFromUtcInclusive), "The due range start must be less than or equal to range end.");
        }

        if (filter.OverdueBy < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(filter.OverdueBy), "Overdue threshold must be non-negative.");
        }

        if (filter.MissedBy < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(filter.MissedBy), "Missed threshold must be non-negative.");
        }

        if (filter.Priority is { } priority && !Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(filter.Priority), priority, "Invalid reminder priority.");
        }

        if (filter.Action is { } action && !Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(filter.Action), action, "Invalid missed reminder action.");
        }

        if (filter.ScheduleKind is { } scheduleKind && !Enum.IsDefined(scheduleKind))
        {
            throw new ArgumentOutOfRangeException(nameof(filter.ScheduleKind), scheduleKind, "Invalid reminder schedule kind.");
        }

        const ReminderQueryStatus supportedStatuses =
            ReminderQueryStatus.Due |
            ReminderQueryStatus.Overdue |
            ReminderQueryStatus.Missed |
            ReminderQueryStatus.Upcoming;

        if ((filter.Status & ~supportedStatuses) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(filter.Status), filter.Status, "Invalid reminder status mask.");
        }
    }

    private static Orleans.Runtime.ReminderScheduleKind GetScheduleKind(ReminderEntry reminder)
        => string.IsNullOrWhiteSpace(reminder.CronExpression)
            ? Orleans.Runtime.ReminderScheduleKind.Interval
            : Orleans.Runtime.ReminderScheduleKind.Cron;

    private static bool MatchesStatus(
        ReminderEntry reminder,
        DateTime due,
        DateTime now,
        DateTime overdueThreshold,
        DateTime missedThreshold,
        ReminderQueryStatus status)
    {
        if (status == ReminderQueryStatus.Any)
        {
            return true;
        }

        if ((status & ReminderQueryStatus.Due) != 0 && due <= now)
        {
            return true;
        }

        if ((status & ReminderQueryStatus.Overdue) != 0 && due <= overdueThreshold)
        {
            return true;
        }

        if ((status & ReminderQueryStatus.Missed) != 0 && due <= missedThreshold && (reminder.LastFireUtc is null || reminder.LastFireUtc < due))
        {
            return true;
        }

        if ((status & ReminderQueryStatus.Upcoming) != 0 && due > now)
        {
            return true;
        }

        return false;
    }

    private static DateTime? CalculateNextDue(ReminderEntry entry, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(entry.CronExpression))
        {
            var schedule = ReminderCronSchedule.Parse(entry.CronExpression, entry.CronTimeZoneId);
            return schedule.GetNextOccurrence(now);
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

    private async Task<string> UpsertRowOrThrow(GrainId grainId, string name, ReminderEntry entry, string operation)
    {
        var etag = await reminderTable.UpsertRow(entry);
        if (etag is null)
        {
            throw CreateConcurrencyException(operation, grainId, name);
        }

        return etag;
    }

    private static Runtime.ReminderException CreateConcurrencyException(string operation, GrainId grainId, string name)
        => new($"{operation} failed due to an ETag conflict for reminder '{name}' on grain '{grainId}'. Retry the operation.");

    private static void EnsureUtc(DateTime value, string argumentName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("DateTime must use DateTimeKind.Utc.", argumentName);
        }
    }

    private static IEnumerable<HashScanRange> EnumerateHashScanRanges()
    {
        const ulong ringSize = (ulong)uint.MaxValue + 1;
        var baseSegmentSize = ringSize / ScanSegmentCount;
        var remainder = ringSize % ScanSegmentCount;

        ulong nextStart = 0;
        for (var i = 0; i < ScanSegmentCount; i++)
        {
            var segmentSize = baseSegmentSize + (i < (int)remainder ? 1UL : 0UL);
            var endExclusive = nextStart + segmentSize;

            var begin = nextStart == 0 ? uint.MaxValue : (uint)(nextStart - 1);
            var end = (uint)(endExclusive - 1);
            yield return new HashScanRange(begin, end);

            nextStart = endExclusive;
        }
    }

    private readonly struct ReminderListCandidate(string grainId, string reminderName, ReminderEntry reminder)
    {
        public string GrainId { get; } = grainId;
        public string ReminderName { get; } = reminderName;
        public ReminderEntry Reminder { get; } = reminder;
    }

    private sealed class ReminderListCandidateComparer : IComparer<ReminderListCandidate>
    {
        public int Compare(ReminderListCandidate x, ReminderListCandidate y)
        {
            var grainCompare = string.CompareOrdinal(x.GrainId, y.GrainId);
            if (grainCompare != 0)
            {
                return grainCompare;
            }

            return string.CompareOrdinal(x.ReminderName, y.ReminderName);
        }
    }

    private sealed class ReverseReminderListCandidateComparer : IComparer<ReminderListCandidate>
    {
        public int Compare(ReminderListCandidate x, ReminderListCandidate y)
        {
            var compare = ListCandidateComparer.Compare(x, y);
            if (compare < 0)
            {
                return 1;
            }

            if (compare > 0)
            {
                return -1;
            }

            return 0;
        }
    }

    private sealed class UpcomingReminderComparer : IComparer<ReminderEntry>
    {
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

            var priorityCompare = y.Priority.CompareTo(x.Priority);
            if (priorityCompare != 0)
            {
                return priorityCompare;
            }

            return (x.NextDueUtc ?? x.StartAt).CompareTo(y.NextDueUtc ?? y.StartAt);
        }
    }

    private readonly record struct HashScanRange(uint Begin, uint End);
}
