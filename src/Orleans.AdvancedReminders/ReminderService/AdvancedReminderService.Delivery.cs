using Microsoft.Extensions.Logging;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed partial class AdvancedReminderService
{
    internal async Task ProcessDueReminderCoreAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        CancellationToken cancellationToken,
        int durableJobDequeueCount = 0)
    {
        var preparation = await PrepareReminderDeliveryCoreAsync(
            grainId, reminderName, expectedScheduleId, durableJobDequeueCount, cancellationToken);
        if (preparation.Outcome != AdvancedReminderDeliveryOutcome.Ready)
        {
            return;
        }

        var deliveryException = await DeliverReminderCallbackAsync(
            preparation.Entry!, preparation.CallbackTime, cancellationToken);
        await CompleteReminderDeliveryCoreAsync(
            preparation.Entry!, preparation.CallbackTime, deliveryException, durableJobDequeueCount, cancellationToken);
    }

    internal async Task<AdvancedReminderDeliveryPreparation> PrepareReminderDeliveryCoreAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        int durableJobDequeueCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = await ReminderTableRead.WaitAsync(
            _reminderTable.ReadRow(grainId, reminderName), _timeProvider, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (entry is null || !MatchesScheduledOccurrence(entry, expectedScheduleId))
        {
            return new AdvancedReminderDeliveryPreparation();
        }

        var now = GetUtcNow();
        var due = entry.NextDueUtc ?? entry.StartAt;
        if (due > now)
        {
            // A job can become observable before its due time after a clock adjustment.
            // Replace it with a new occurrence instead of firing the reminder early.
            PrepareNextOccurrence(entry);
            await PersistAndScheduleCoreAsync(entry, cancellationToken);
            return new AdvancedReminderDeliveryPreparation();
        }

        if (_options.DeleteReminderWhenGrainTypeIsUnavailable && IsGrainTypeUnavailable(grainId.Type))
        {
            await RemoveReminderAsync(entry);
            _logger.LogWarning(
                "Deleted reminder {ReminderName} for grain {GrainId} because no active silo declares grain type {GrainType}.",
                entry.ReminderName,
                entry.GrainId,
                grainId.Type);
            return new AdvancedReminderDeliveryPreparation();
        }

        var overdueBy = now > due ? now - due : TimeSpan.Zero;
        var isMissed = overdueBy > _options.MissedReminderGracePeriod;
        if (durableJobDequeueCount <= 1
            && isMissed
            && !IsRetainedFailedDelivery(entry)
            && entry.Action != Runtime.MissedReminderAction.FireImmediately)
        {
            if (entry.Action == Runtime.MissedReminderAction.Notify)
            {
                _logger.LogWarning(
                    "Reminder {ReminderName} for grain {GrainId} missed due window at {Due}. Current time {Now}.",
                    reminderName,
                    grainId,
                    due,
                    now);
            }

            await AdvanceReminderOccurrenceAsync(entry, now, cancellationToken);
            return new AdvancedReminderDeliveryPreparation();
        }

        return new AdvancedReminderDeliveryPreparation
        {
            Outcome = AdvancedReminderDeliveryOutcome.Ready,
            Entry = entry,
            CallbackTime = now
        };
    }

    internal async Task<Exception?> DeliverReminderCallbackAsync(
        ReminderEntry entry,
        DateTime callbackTime,
        CancellationToken cancellationToken)
    {
        var status = new Runtime.TickStatus(
            entry.StartAt,
            string.IsNullOrWhiteSpace(entry.CronExpression) ? entry.Period : TimeSpan.Zero,
            callbackTime);
        try
        {
            var remindable = _grainFactory.GetGrain<IRemindable>(entry.GrainId);
            await remindable.ReceiveReminder(entry.ReminderName, status);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Error delivering reminder {ReminderName} to grain {GrainId}.",
                entry.ReminderName,
                entry.GrainId);
            return exception;
        }
    }

    internal async Task CompleteReminderDeliveryCoreAsync(
        ReminderEntry entry,
        DateTime callbackTime,
        Exception? deliveryException,
        int durableJobDequeueCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Independent registration and removal can run while the adapter waits
        // for the callback. Never overwrite their newer registration or occurrence.
        var current = await ReminderTableRead.WaitAsync(
            _reminderTable.ReadRow(entry.GrainId, entry.ReminderName), _timeProvider, cancellationToken);
        if (current is null
            || !string.Equals(current.ETag, entry.ETag, StringComparison.Ordinal)
            || !string.Equals(current.ScheduleId, entry.ScheduleId, StringComparison.Ordinal))
        {
            return;
        }

        entry.LastFireUtc = callbackTime;
        if (deliveryException is not null
            && durableJobDequeueCount > 0
            && _options.DeliveryRetryPolicy.TryGetAttemptLimit(out var maximumDeliveryAttempts))
        {
            if (durableJobDequeueCount < maximumDeliveryAttempts)
            {
                throw new ReminderDeliveryException(entry.GrainId, entry.ReminderName, durableJobDequeueCount, deliveryException);
            }

            await RemoveReminderAsync(entry);
            _logger.LogWarning(
                "Deleted reminder {ReminderName} for grain {GrainId} after its callback failed at Durable Jobs dequeue count {DequeueCount}.",
                entry.ReminderName,
                entry.GrainId,
                durableJobDequeueCount);
            return;
        }

        if (deliveryException is not null
            && _options.DeliveryRetryPolicy.RetainsFailedDeliveries)
        {
            // A callback can fail before its owner starts or rearms the next attempt.
            // Persist the replacement occurrence before completing this job so recovery
            // can restore a missing handle and stale deliveries remain fenced.
            var deliveryDueUtc = GetUtcNow().Add(_options.MinimumReminderPeriod);
            if (entry.Period == TimeSpan.Zero && string.IsNullOrWhiteSpace(entry.CronExpression))
            {
                entry.NextDueUtc = deliveryDueUtc;
            }
            // Keep a recurring occurrence's logical due time until it succeeds.
            // Moving its calendar anchor would discard catch-up or drift intervals.
            PrepareNextOccurrence(entry);
            await PersistAndScheduleCoreAsync(entry, cancellationToken, deliveryDueUtc);
            return;
        }

        await AdvanceReminderOccurrenceAsync(entry, callbackTime, cancellationToken);
    }

    private async Task AdvanceReminderOccurrenceAsync(ReminderEntry entry, DateTime callbackTime, CancellationToken cancellationToken)
    {
        var nextDue = CalculateNextDue(entry, callbackTime);
        if (nextDue is null)
        {
            if (!await _reminderTable.RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag))
            {
                throw new Runtime.ReminderException($"Could not remove completed reminder '{entry.ReminderName}' for grain '{entry.GrainId}' due to ETag mismatch.");
            }

            return;
        }

        entry.NextDueUtc = nextDue;
        PrepareNextOccurrence(entry);
        await PersistAndScheduleCoreAsync(entry, cancellationToken);
    }

    private bool IsGrainTypeUnavailable(GrainType grainType)
    {
        if (GenericGrainType.TryParse(grainType, out var generic) && generic.IsConstructed)
        {
            grainType = generic.GetUnconstructedGrainType().GrainType;
        }

        var membershipBefore = _clusterMembershipService.CurrentSnapshot;
        var clusterManifest = _clusterManifestProvider.Current;
        var membershipAfter = _clusterMembershipService.CurrentSnapshot;
        if (membershipBefore.Version != membershipAfter.Version
            || clusterManifest.Version.Major != membershipAfter.Version.Value)
        {
            // Membership changed while reading, or the manifest belongs to another membership
            // version. Neither case proves that the grain type has been retired.
            return false;
        }

        var activeSiloSeen = false;
        foreach (var (siloAddress, member) in membershipAfter.Members)
        {
            if (siloAddress.IsClient || member.Status != SiloStatus.Active)
            {
                continue;
            }

            activeSiloSeen = true;
            if (!clusterManifest.Silos.TryGetValue(siloAddress, out var siloManifest))
            {
                // ClusterManifestProvider publishes the membership version before it has
                // necessarily retrieved every active silo manifest. Fail closed until complete.
                return false;
            }

            if (siloManifest.Grains.ContainsKey(grainType))
            {
                return false;
            }
        }

        // An empty active server set is not proof that a type has been retired.
        return activeSiloSeen;
    }

    private async Task RemoveReminderAsync(ReminderEntry entry)
    {
        if (!await _reminderTable.RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag))
        {
            throw new Runtime.ReminderException(
                $"Could not remove reminder '{entry.ReminderName}' for grain '{entry.GrainId}' due to ETag mismatch.");
        }
    }

    private static bool HasFutureSchedule(ReminderEntry entry)
        => entry.NextDueUtc is not null || entry.Period > TimeSpan.Zero || !string.IsNullOrWhiteSpace(entry.CronExpression);

    private bool IsRetainedFailedDelivery(ReminderEntry entry)
        => _options.DeliveryRetryPolicy.RetainsFailedDeliveries
            && entry.LastFireUtc is { } lastFireUtc
            // Successful one-shots are removed, so any persisted attempt belongs to a retry.
            // Recurring schedules advance their logical due time only after success.
            && ((entry.Period == TimeSpan.Zero && string.IsNullOrWhiteSpace(entry.CronExpression))
                || lastFireUtc >= (entry.NextDueUtc ?? entry.StartAt));

    private static bool MatchesScheduledOccurrence(ReminderEntry entry, string? expectedScheduleId)
        => string.IsNullOrEmpty(expectedScheduleId)
            || string.Equals(entry.ScheduleId, expectedScheduleId, StringComparison.Ordinal)
            || (string.IsNullOrEmpty(entry.ScheduleId) && string.Equals(entry.ETag, expectedScheduleId, StringComparison.Ordinal));
}
