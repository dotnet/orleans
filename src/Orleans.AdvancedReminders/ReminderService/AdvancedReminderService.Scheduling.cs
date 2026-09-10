using Microsoft.Extensions.Logging;
using Orleans.AdvancedReminders.Cron.Internal;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed partial class AdvancedReminderService
{
    internal async Task EnsureScheduledCoreAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        bool force,
        CancellationToken cancellationToken)
    {
        var entry = await _reminderTable.ReadRow(grainId, reminderName);
        if (entry is null || !HasFutureSchedule(entry))
        {
            return;
        }

        if (!string.IsNullOrEmpty(expectedScheduleId)
            && !string.Equals(entry.ScheduleId, expectedScheduleId, StringComparison.Ordinal))
        {
            return;
        }

        if (!force && !string.IsNullOrEmpty(entry.JobId) && !string.IsNullOrEmpty(entry.JobShardId))
        {
            return;
        }

        // The durable job can have been persisted even when persisting its handle failed.
        // Rotate the occurrence token before every repair so that any orphaned job becomes
        // harmless when it eventually runs instead of delivering the same occurrence twice.
        PrepareNextOccurrence(entry);
        entry.JobId = string.Empty;
        entry.JobShardId = string.Empty;
        entry.ETag = await _reminderTable.UpsertRow(entry);
        await ScheduleAndPersistHandleAsync(entry, cancellationToken);
    }

    private async Task PersistAndScheduleCoreAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        entry.ETag = await _reminderTable.UpsertRow(entry);
        if (HasFutureSchedule(entry))
        {
            await ScheduleAndPersistHandleAsync(entry, cancellationToken);
        }
    }

    private async Task ScheduleAndPersistHandleAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        var due = entry.NextDueUtc ?? entry.StartAt;
        var dueTime = new DateTimeOffset(due, TimeSpan.Zero);
        var grainIdText = entry.GrainId.ToString();
        var dispatcher = GetDispatcher(entry.GrainId);
        var job = await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = dispatcher.GetGrainId(),
                JobName = string.Concat(JobNamePrefix, entry.ReminderName),
                DueTime = dueTime,
                Metadata = new Dictionary<string, string>(capacity: 3, comparer: StringComparer.Ordinal)
                {
                    [GrainIdMetadataKey] = grainIdText,
                    [ReminderNameMetadataKey] = entry.ReminderName,
                    [ScheduleIdMetadataKey] = entry.ScheduleId,
                },
            },
            cancellationToken);

        entry.JobId = job.Id;
        entry.JobShardId = job.ShardId;
        entry.ETag = await _reminderTable.UpsertRow(entry);
    }

    private async Task CancelScheduledJobAsync(ReminderEntry entry)
    {
        if (string.IsNullOrEmpty(entry.JobId) || string.IsNullOrEmpty(entry.JobShardId))
        {
            return;
        }

        try
        {
            var canceled = await _jobManager.CancelAsync(
                new DurableJob
                {
                    Id = entry.JobId,
                    Name = string.Concat(JobNamePrefix, entry.ReminderName),
                    ShardId = entry.JobShardId,
                    DueTime = new DateTimeOffset(entry.NextDueUtc ?? entry.StartAt, TimeSpan.Zero),
                    TargetGrainId = GetDispatcher(entry.GrainId).GetGrainId(),
                },
                CancellationToken.None);

            if (!canceled)
            {
                _logger.LogWarning(
                    "Durable job {JobId} for reminder {ReminderName} on grain {GrainId} could not be canceled. The stale job will be ignored by its schedule id.",
                    entry.JobId,
                    entry.ReminderName,
                    entry.GrainId);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Durable job {JobId} for reminder {ReminderName} on grain {GrainId} could not be canceled after the reminder mutation committed. The stale job will be ignored by its schedule id.",
                entry.JobId,
                entry.ReminderName,
                entry.GrainId);
        }
    }

    private static void PrepareNextOccurrence(ReminderEntry entry)
    {
        entry.ScheduleId = ReminderScheduleId.RotateOccurrence(entry.ScheduleId);
        entry.JobId = string.Empty;
        entry.JobShardId = string.Empty;
    }

    private static void PrepareNewSchedule(ReminderEntry entry, string? declarationId = null)
    {
        entry.ScheduleId = ReminderScheduleId.Create(declarationId);
        entry.JobId = string.Empty;
        entry.JobShardId = string.Empty;
    }

    private static bool HasAttributeDeclaration(string scheduleId, string declarationId)
        => ReminderScheduleId.HasAttributeDeclaration(scheduleId, declarationId);

    private ReminderEntry CreateIntervalEntry(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.MissedReminderAction action)
    {
        var period = schedule.Period!.Value;
        var dueAtUtc = schedule.DueAtUtc ?? GetUtcNow().Add(schedule.DueTime!.Value);
        return new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = reminderName,
            StartAt = dueAtUtc,
            Period = period,
            Action = action,
            NextDueUtc = dueAtUtc,
            LastFireUtc = null,
        };
    }

    private ReminderEntry CreateCronEntry(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.MissedReminderAction action)
    {
        var cronSchedule = ReminderCronSchedule.Parse(schedule.CronExpression!, schedule.CronTimeZoneId);
        var nextDue = cronSchedule.GetNextOccurrence(GetUtcNow(), inclusive: true)
            ?? throw new Runtime.ReminderException($"Reminder '{reminderName}' has no future cron occurrences.");

        return new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = reminderName,
            StartAt = nextDue,
            Period = TimeSpan.Zero,
            CronExpression = cronSchedule.Expression.ToExpressionString(),
            CronTimeZoneId = cronSchedule.TimeZoneId ?? string.Empty,
            Action = action,
            NextDueUtc = nextDue,
            LastFireUtc = null,
        };
    }

    internal static DateTime? CalculateNextDue(ReminderEntry entry, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(entry.CronExpression))
        {
            var cronSchedule = ReminderCronSchedule.Parse(entry.CronExpression, entry.CronTimeZoneId);
            return cronSchedule.GetNextOccurrence(now);
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
            if (periodsBehind > (DateTime.MaxValue.Ticks - next.Ticks) / entry.Period.Ticks)
            {
                return null;
            }

            next = next.AddTicks(periodsBehind * entry.Period.Ticks);
        }

        return next;
    }
}
