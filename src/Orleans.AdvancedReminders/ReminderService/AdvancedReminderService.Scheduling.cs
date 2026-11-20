using Microsoft.Extensions.Logging;
using Orleans.AdvancedReminders.Cron.Internal;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed partial class AdvancedReminderService
{
    private static readonly TimeSpan ScheduledJobCancellationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScheduledJobCreationTimeout = TimeSpan.FromMinutes(1);

    internal async Task EnsureScheduledCoreAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        bool force,
        CancellationToken cancellationToken,
        bool recoverOverdue = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = await ReminderTableRead.WaitAsync(
            _reminderTable.ReadRow(grainId, reminderName), _timeProvider, cancellationToken);
        if (entry is null || !HasFutureSchedule(entry))
        {
            return;
        }

        if (!string.IsNullOrEmpty(expectedScheduleId)
            && !string.Equals(entry.ScheduleId, expectedScheduleId, StringComparison.Ordinal))
        {
            return;
        }

        var now = GetUtcNow();
        var hasJob = !string.IsNullOrEmpty(entry.JobId) && !string.IsNullOrEmpty(entry.JobShardId);
        var overdue = recoverOverdue && hasJob && ShouldRecoverOverdueOccurrence(entry, now, _options);
        if (!force && hasJob && !overdue)
        {
            return;
        }

        var deliveryDueUtc = ReminderScheduleId.GetDeliveryDueUtc(entry.ScheduleId);
        if (deliveryDueUtc is null && IsRetainedFailedDelivery(entry))
        {
            var due = entry.NextDueUtc ?? entry.StartAt;
            // Legacy recurring retries retained the schedule's due time. Give a replacement
            // a full retry interval so it cannot run before the old carrier was due.
            var retryDue = IsRecurring(entry) ? now.Add(_options.MinimumReminderPeriod) : due;
            deliveryDueUtc = retryDue > due ? retryDue : due;
        }

        if ((overdue && deliveryDueUtc is null) || deliveryDueUtc < now)
        {
            deliveryDueUtc = now;
        }

        // Rotate before persisting the replacement so a previous job cannot deliver it.
        PrepareNextOccurrence(entry, deliveryDueUtc);
        await PersistAndScheduleCoreAsync(entry, cancellationToken);
        if (overdue)
        {
            _logger.LogWarning(
                "Rescheduled overdue reminder {ReminderName} for grain {GrainId}; its occurrence was due at {Due}.",
                reminderName, grainId, entry.NextDueUtc ?? entry.StartAt);
        }
    }

    internal static bool ShouldRecoverOverdueOccurrence(ReminderEntry entry, DateTime now, ReminderOptions options)
    {
        if (!options.DeliveryRetryPolicy.RetainsFailedDeliveries || !HasFutureSchedule(entry))
        {
            return false;
        }

        var due = entry.NextDueUtc ?? entry.StartAt;
        if (ReminderScheduleId.GetDeliveryDueUtc(entry.ScheduleId) is { } deliveryDueUtc)
        {
            return now - deliveryDueUtc > options.MissedReminderGracePeriod;
        }

        if (IsRecurring(entry) && entry.LastFireUtc is { } lastFire && lastFire >= due)
        {
            // Rows written by older versions did not capture the retry's delivery deadline.
            var sinceAttempt = now - lastFire;
            return sinceAttempt > options.MinimumReminderPeriod
                && sinceAttempt - options.MinimumReminderPeriod > options.MissedReminderGracePeriod;
        }

        return now - due > options.MissedReminderGracePeriod;
    }

    private async Task PersistAndScheduleCoreAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        entry.ETag = await _tableMutations.UpsertAsync(entry, cancellationToken);
        if (HasFutureSchedule(entry))
        {
            await ScheduleAndPersistHandleAsync(entry, cancellationToken);
        }
    }

    private async Task ScheduleAndPersistHandleAsync(ReminderEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var due = ReminderScheduleId.GetDeliveryDueUtc(entry.ScheduleId) ?? entry.NextDueUtc ?? entry.StartAt;
        var dueTime = new DateTimeOffset(due, TimeSpan.Zero);
        var grainIdText = entry.GrainId.ToString();
        var dispatcher = GetDispatcher(entry.GrainId);
        var job = await ScheduleJobAsync(
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
        entry.ETag = await _tableMutations.UpsertAsync(entry, cancellationToken);
    }

    private async Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(ScheduledJobCreationTimeout, _timeProvider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            // A late scheduling result must never resume handle persistence after
            // this owner turn has ended. Recovery rotates the pending occurrence
            // before scheduling another job, fencing any job created in the meantime.
            var job = await _jobManager.ScheduleJobAsync(request, cancellation.Token).WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            return job;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Scheduling durable job '{request.JobName}' exceeded the timeout of {ScheduledJobCreationTimeout}.",
                exception);
        }
    }

    private async Task CancelScheduledJobAsync(ReminderEntry entry)
    {
        if (string.IsNullOrEmpty(entry.JobId) || string.IsNullOrEmpty(entry.JobShardId))
        {
            return;
        }

        // The reminder mutation has already committed and fences the old job.
        // Bound this best-effort cleanup even when the old shard owner or its
        // storage stops responding, so it cannot occupy the dispatcher forever.
        using var cancellation = new CancellationTokenSource(ScheduledJobCancellationTimeout, _timeProvider);
        try
        {
            var canceled = await _jobManager.CancelAsync(
                new DurableJob
                {
                    Id = entry.JobId,
                    Name = string.Concat(JobNamePrefix, entry.ReminderName),
                    ShardId = entry.JobShardId,
                    DueTime = new DateTimeOffset(ReminderScheduleId.GetDeliveryDueUtc(entry.ScheduleId) ?? entry.NextDueUtc ?? entry.StartAt, TimeSpan.Zero),
                    TargetGrainId = GetDispatcher(entry.GrainId).GetGrainId(),
                },
                cancellation.Token).WaitAsync(cancellation.Token);

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

    private static void PrepareNextOccurrence(ReminderEntry entry, DateTime? deliveryDueUtc = null)
    {
        entry.ScheduleId = ReminderScheduleId.RotateOccurrence(entry.ScheduleId, deliveryDueUtc);
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
