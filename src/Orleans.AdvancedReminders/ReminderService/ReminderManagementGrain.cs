using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Jobs;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders;

/// <summary>
/// Administrative management API for advanced reminders.
/// </summary>
[method: ActivatorUtilitiesConstructor]
public sealed class ReminderManagementGrain(
    IReminderTable reminderTable,
    [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider timeProvider) : Grain, IReminderManagementGrain
{
    private const int ScanBucketCount = 256;
    private const int MaxPageSize = 4_096;
    private const int ProviderPageSize = 256;
    private const ulong ScanBucketWidth = (ulong)uint.MaxValue / ScanBucketCount + 1;
    private readonly IReminderTable _reminderTable = reminderTable;
    private readonly ReminderTableMutations _tableMutations = new(reminderTable, timeProvider);
    private readonly TimeProvider _timeProvider = timeProvider;

    public ReminderManagementGrain(IReminderTable reminderTable)
        : this(reminderTable, TimeProvider.System)
    {
    }

    internal ReminderManagementGrain(IReminderTable reminderTable, IServiceProvider? serviceProvider, TimeProvider? timeProvider = null)
        : this(reminderTable, timeProvider ?? TimeProvider.System)
    {
        _serviceProvider = serviceProvider;
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
        if (pageSize is <= 0 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        ValidateFilter(filter);

        var cursor = ReminderCursor.Parse(continuationToken);
        var now = GetUtcNow();
        var reminders = new List<ReminderEntry>(pageSize);
        var providerToken = cursor?.ProviderToken;
        // Some providers require the original limit when resuming their continuation.
        var providerPageSize = cursor?.PageSize ?? Math.Min(pageSize, ProviderPageSize);
        var offset = cursor?.Offset ?? 0;
        for (var bucket = cursor?.Bucket ?? 0; bucket < ScanBucketCount; bucket++)
        {
            var begin = bucket == 0 ? uint.MaxValue : (uint)((ulong)bucket * ScanBucketWidth - 1);
            var end = (uint)((ulong)(bucket + 1) * ScanBucketWidth - 1);
            do
            {
                var page = await ReminderTableRead.WaitAsync(
                    _reminderTable.ReadRows(begin, end, providerPageSize, providerToken), _timeProvider);
                // A replayed provider page can contain fewer rows than its original response.
                var start = Math.Min(offset, page.Reminders.Count);
                offset -= start;
                for (var index = start; index < page.Reminders.Count; index++)
                {
                    var reminder = page.Reminders[index];
                    if (!MatchesFilter(reminder, filter, now))
                    {
                        continue;
                    }

                    if (reminders.Count == pageSize)
                    {
                        return new ReminderManagementPage
                        {
                            Reminders = reminders,
                            ContinuationToken = ReminderCursor.Create(bucket, providerPageSize, index, providerToken),
                        };
                    }

                    reminders.Add(reminder);
                }

                providerToken = page.ContinuationToken;
            } while (providerToken is not null);

            offset = 0;
        }

        return new ReminderManagementPage { Reminders = reminders };
    }

    public async Task<IEnumerable<ReminderEntry>> ListForGrainAsync(GrainId grainId)
        => (await ReminderTableRead.WaitAsync(_reminderTable.ReadRows(grainId), _timeProvider))
            .Reminders.OrderBy(reminder => reminder, ReminderEntryComparer.Instance).ToList();

    public async Task SetActionAsync(GrainId grainId, string name, Runtime.MissedReminderAction action)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Invalid missed reminder action.");
        }

        var entry = await GetEntryAsync(grainId, name);
        entry.Action = action;
        await PersistMutationAsync(entry);
    }

    public async Task RepairAsync(GrainId grainId, string name)
    {
        var entry = await GetEntryAsync(grainId, name);
        entry.NextDueUtc = Runtime.ReminderService.AdvancedReminderService.CalculateNextDue(entry, GetUtcNow())
            ?? entry.NextDueUtc
            ?? entry.StartAt;
        await PersistMutationAsync(entry);
    }

    public async Task DeleteAsync(GrainId grainId, string name)
    {
        var entry = await GetEntryAsync(grainId, name);
        var reminderService = GetReminderService();
        if (reminderService is not null)
        {
            await reminderService.UnregisterReminder(entry.ToIGrainReminder());
            return;
        }

        if (!await _tableMutations.RemoveAsync(grainId, name, entry.ETag))
        {
            throw new Runtime.ReminderException(
                $"Could not delete reminder '{name}' for grain '{grainId}' due to ETag mismatch.");
        }
    }

    private async Task<ReminderEntry> GetEntryAsync(GrainId grainId, string name)
        => await ReminderTableRead.WaitAsync(_reminderTable.ReadRow(grainId, name), _timeProvider)
            ?? throw new Runtime.ReminderException($"Reminder '{name}' for grain '{grainId}' was not found.");

    private async Task PersistMutationAsync(ReminderEntry entry)
    {
        var reminderService = GetReminderService();
        if (reminderService is null)
        {
            entry.ETag = await _tableMutations.UpsertAsync(entry);
            return;
        }

        await reminderService.UpsertAndScheduleEntryAsync(entry, CancellationToken.None);
    }

    private Runtime.ReminderService.AdvancedReminderService? GetReminderService()
    {
        var serviceProvider = _serviceProvider ?? ServiceProvider;
        return serviceProvider?.GetService(typeof(Runtime.ReminderService.AdvancedReminderService))
            as Runtime.ReminderService.AdvancedReminderService;
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

        if (filter.Action is { } action && reminder.Action != action)
        {
            return false;
        }

        if (filter.ScheduleKind is { } scheduleKind && GetScheduleKind(reminder) != scheduleKind)
        {
            return false;
        }

        if (filter.GrainType is { } grainType && reminder.GrainId.Type != grainType)
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

        if ((filter.Status & ReminderQueryStatus.Overdue) != 0 && due <= SubtractClamped(now, filter.OverdueBy))
        {
            matched = true;
        }

        if ((filter.Status & ReminderQueryStatus.Missed) != 0
            && due <= SubtractClamped(now, filter.MissedBy)
            && (reminder.LastFireUtc is null || reminder.LastFireUtc < due))
        {
            matched = true;
        }

        return matched;
    }

    private static void ValidateFilter(ReminderQueryFilter filter)
    {
        ValidateUtc(filter.DueFromUtcInclusive, nameof(filter.DueFromUtcInclusive));
        ValidateUtc(filter.DueToUtcInclusive, nameof(filter.DueToUtcInclusive));

        if (filter.DueFromUtcInclusive > filter.DueToUtcInclusive)
        {
            throw new ArgumentException("The due-time lower bound must not be later than the upper bound.", nameof(filter));
        }

        if (filter.Action is { } action && !Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), action, "Invalid missed reminder action filter.");
        }

        if (filter.ScheduleKind is { } scheduleKind && !Enum.IsDefined(scheduleKind))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), scheduleKind, "Invalid reminder schedule kind filter.");
        }

        const ReminderQueryStatus allStatuses =
            ReminderQueryStatus.Due | ReminderQueryStatus.Overdue | ReminderQueryStatus.Missed | ReminderQueryStatus.Upcoming;
        if ((filter.Status & ~allStatuses) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(filter), filter.Status, "Invalid reminder query status filter.");
        }

        if (filter.OverdueBy < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(filter), filter.OverdueBy, "The overdue threshold must be non-negative.");
        }

        if (filter.MissedBy < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(filter), filter.MissedBy, "The missed threshold must be non-negative.");
        }

        static void ValidateUtc(DateTime? value, string propertyName)
        {
            if (value is { Kind: not DateTimeKind.Utc })
            {
                throw new ArgumentException($"{propertyName} must use DateTimeKind.Utc.", nameof(filter));
            }
        }
    }

    private static DateTime SubtractClamped(DateTime value, TimeSpan amount)
        => amount > value - DateTime.MinValue ? DateTime.MinValue : value.Subtract(amount);

    private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static DateTime GetDueTime(ReminderEntry reminder) => reminder.NextDueUtc ?? reminder.StartAt;

    private static Runtime.ReminderScheduleKind GetScheduleKind(ReminderEntry reminder)
        => string.IsNullOrWhiteSpace(reminder.CronExpression)
            ? Runtime.ReminderScheduleKind.Interval
            : Runtime.ReminderScheduleKind.Cron;

    private sealed record ReminderCursor(int Bucket, int PageSize, int Offset, string? ProviderToken)
    {
        public static string Create(int bucket, int pageSize, int offset, string? providerToken)
        {
            var payload = string.Create(CultureInfo.InvariantCulture, $"4\n{bucket}\n{pageSize}\n{offset}");
            if (providerToken is not null)
            {
                payload = string.Concat(payload, "\n", providerToken);
            }

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
                var parts = payload.Split('\n', 5);
                if (parts.Length < 4 || parts[0] != "4"
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bucket)
                    || bucket < 0 || bucket >= ScanBucketCount
                    || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize)
                    || pageSize is <= 0 or > ProviderPageSize
                    || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                    || offset < 0 || offset >= pageSize)
                {
                    throw new FormatException("Continuation token version or provider position is invalid.");
                }

                return new ReminderCursor(bucket, pageSize, offset, parts.Length == 5 ? parts[4] : null);
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("Invalid continuation token.", nameof(continuationToken), exception);
            }
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

            var grainCompare = x.GrainId.CompareTo(y.GrainId);
            if (grainCompare != 0)
            {
                return grainCompare;
            }

            return string.CompareOrdinal(x.ReminderName, y.ReminderName);
        }
    }
}
