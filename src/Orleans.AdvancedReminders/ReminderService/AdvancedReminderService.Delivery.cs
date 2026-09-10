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
        var entry = await _reminderTable.ReadRow(grainId, reminderName);
        if (entry is null || !MatchesScheduledOccurrence(entry, expectedScheduleId))
        {
            return;
        }

        var now = GetUtcNow();
        var due = entry.NextDueUtc ?? entry.StartAt;
        if (due > now)
        {
            // A job can become observable before its due time after a clock adjustment.
            // Replace it with a new occurrence instead of firing the reminder early.
            PrepareNextOccurrence(entry);
            await PersistAndScheduleCoreAsync(entry, cancellationToken);
            return;
        }

        if (_options.DeleteReminderWhenGrainTypeIsUnavailable && IsGrainTypeUnavailable(grainId.Type))
        {
            await RemoveReminderAsync(entry);
            _logger.LogWarning(
                "Deleted reminder {ReminderName} for grain {GrainId} because no active silo declares grain type {GrainType}.",
                entry.ReminderName,
                entry.GrainId,
                grainId.Type);
            return;
        }

        var overdueBy = now > due ? now - due : TimeSpan.Zero;
        var isMissed = overdueBy > _options.MissedReminderGracePeriod;

        var shouldFire = true;
        if (durableJobDequeueCount <= 1
            && isMissed
            && entry.Action != Runtime.MissedReminderAction.FireImmediately)
        {
            shouldFire = false;
            if (entry.Action == Runtime.MissedReminderAction.Notify)
            {
                _logger.LogWarning(
                    "Reminder {ReminderName} for grain {GrainId} missed due window at {Due}. Current time {Now}.",
                    reminderName,
                    grainId,
                    due,
                    now);
            }
        }

        if (shouldFire)
        {
            var remindable = _grainFactory.GetGrain<IRemindable>(grainId);
            var status = new Runtime.TickStatus(
                entry.StartAt,
                string.IsNullOrWhiteSpace(entry.CronExpression) ? entry.Period : TimeSpan.Zero,
                now);
            Exception? deliveryException = null;
            try
            {
                await remindable.ReceiveReminder(entry.ReminderName, status);
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
                    reminderName,
                    grainId);
                deliveryException = exception;
            }

            entry.LastFireUtc = now;

            // The reminder callback can call back into this dispatcher using the same Orleans call chain.
            // Re-read after the callback so that an unregister or update is not overwritten by this tick.
            var current = await _reminderTable.ReadRow(grainId, reminderName);
            if (current is null
                || !string.Equals(current.ETag, entry.ETag, StringComparison.Ordinal)
                || !string.Equals(current.ScheduleId, entry.ScheduleId, StringComparison.Ordinal))
            {
                return;
            }

            if (deliveryException is not null
                && durableJobDequeueCount > 0
                && _options.MaximumDeliveryAttempts is { } maximumDeliveryAttempts)
            {
                if (durableJobDequeueCount < maximumDeliveryAttempts)
                {
                    throw new ReminderDeliveryException(grainId, reminderName, durableJobDequeueCount, deliveryException);
                }

                await RemoveReminderAsync(entry);
                _logger.LogWarning(
                    "Deleted reminder {ReminderName} for grain {GrainId} after its callback failed at Durable Jobs dequeue count {DequeueCount}.",
                    entry.ReminderName,
                    entry.GrainId,
                    durableJobDequeueCount);
                return;
            }
        }

        var nextDue = CalculateNextDue(entry, now);
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

    private static bool MatchesScheduledOccurrence(ReminderEntry entry, string? expectedScheduleId)
        => string.IsNullOrEmpty(expectedScheduleId)
            || string.Equals(entry.ScheduleId, expectedScheduleId, StringComparison.Ordinal)
            || (string.IsNullOrEmpty(entry.ScheduleId) && string.Equals(entry.ETag, expectedScheduleId, StringComparison.Ordinal));
}
