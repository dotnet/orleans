namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed class AdvancedReminderDispatcherGrain(
    AdvancedReminderService reminderService) : Grain, IAdvancedReminderDispatcherGrain
{
    private readonly AdvancedReminderService _reminderService = reminderService;
    private ActiveDelivery? _activeDelivery;

    public Task<IGrainReminder> RegisterOrUpdateAsync(ReminderEntry entry)
        => _reminderService.RegisterOrUpdateCoreAsync(entry, CancellationToken.None);

    public Task<IGrainReminder> ReconcileAttributeAsync(ReminderEntry entry, string declarationId)
        => _reminderService.ReconcileAttributeCoreAsync(entry, declarationId, CancellationToken.None);

    public Task<string> UpsertAndScheduleAsync(ReminderEntry entry, CancellationToken cancellationToken)
        => _reminderService.UpsertAndScheduleCoreAsync(entry, cancellationToken);

    public Task UnregisterAsync(ReminderData reminder)
        => _reminderService.UnregisterCoreAsync(reminder, CancellationToken.None);

    public async Task ProcessDueReminderAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        int durableJobDequeueCount,
        CancellationToken cancellationToken)
    {
        // This adapter can interleave, but only normal owner requests read or
        // change delivery admission and reminder persistence.
        var owner = GrainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(this.GetPrimaryKeyString());
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Choose the token before admission: a lost reply must not leave an
            // admitted delivery which its caller has no way to release.
            var deliveryToken = Guid.NewGuid();
            var releaseAdmission = true;
            Exception? deliveryException = null;
            var deliveryCancelled = true;
            try
            {
                var preparation = await owner.PrepareDeliveryAsync(
                    grainId, reminderName, expectedScheduleId, durableJobDequeueCount, deliveryToken, cancellationToken);
                releaseAdmission = preparation.Outcome == AdvancedReminderDeliveryOutcome.Ready;
                cancellationToken.ThrowIfCancellationRequested();
                if (preparation.Outcome == AdvancedReminderDeliveryOutcome.Skipped)
                {
                    return;
                }

                if (preparation.Outcome == AdvancedReminderDeliveryOutcome.Wait)
                {
                    await owner.ObserveDeliveryAsync(preparation.DeliveryToken, cancellationToken);
                    continue;
                }

                deliveryException = await _reminderService.DeliverReminderCallbackAsync(
                    preparation.Entry!, preparation.CallbackTime, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                deliveryCancelled = false;
            }
            finally
            {
                if (releaseAdmission)
                {
                    // Normal completion releases only this attempt, including
                    // admission whose reply failed and caller cancellation.
                    await owner.CompleteDeliveryAsync(
                        deliveryToken, deliveryException, deliveryCancelled, CancellationToken.None);
                }
            }

            return;
        }
    }

    public async Task<AdvancedReminderDeliveryPreparation> PrepareDeliveryAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        int durableJobDequeueCount,
        Guid deliveryToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(grainId.ToString(), this.GetPrimaryKeyString(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The reminder recipient does not match its dispatcher.", nameof(grainId));
        }

        if (_activeDelivery is { } active)
        {
            return new AdvancedReminderDeliveryPreparation
            {
                Outcome = AdvancedReminderDeliveryOutcome.Wait,
                DeliveryToken = active.Preparation.DeliveryToken
            };
        }

        var preparation = await _reminderService.PrepareReminderDeliveryCoreAsync(
            grainId, reminderName, expectedScheduleId, durableJobDequeueCount, cancellationToken);
        if (preparation.Outcome == AdvancedReminderDeliveryOutcome.Ready)
        {
            cancellationToken.ThrowIfCancellationRequested();
            preparation = preparation with { DeliveryToken = deliveryToken };
            _activeDelivery = new ActiveDelivery(preparation, durableJobDequeueCount);
        }

        return preparation;
    }

    public Task ObserveDeliveryAsync(Guid deliveryToken, CancellationToken cancellationToken)
        => _activeDelivery is { } active && active.Preparation.DeliveryToken == deliveryToken
            ? active.Completion.Task.WaitAsync(cancellationToken)
            : Task.CompletedTask;

    public async Task CompleteDeliveryAsync(
        Guid deliveryToken,
        Exception? deliveryException,
        bool deliveryCancelled,
        CancellationToken cancellationToken)
    {
        if (_activeDelivery is not { } active || active.Preparation.DeliveryToken != deliveryToken)
        {
            return;
        }

        try
        {
            if (!deliveryCancelled)
            {
                await _reminderService.CompleteReminderDeliveryCoreAsync(
                    active.Preparation.Entry!, active.Preparation.CallbackTime,
                    deliveryException, active.DurableJobDequeueCount, cancellationToken);
            }
        }
        finally
        {
            _activeDelivery = null;
            active.Completion.TrySetResult();
        }
    }

    public Task EnsureScheduledAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        bool force,
        CancellationToken cancellationToken)
        => _reminderService.EnsureScheduledCoreAsync(grainId, reminderName, expectedScheduleId, force, cancellationToken);

    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        if (!AdvancedReminderService.TryGetReminderMetadata(context.Job.Metadata, out var grainId, out var reminderName, out var scheduleId))
        {
            return;
        }

        // The interleaving adapter waits for normal delivery preparation, so
        // registration and durable-job handle persistence finish before a tick.
        using var reentrancy = RequestContext.SuppressCallChainReentrancy();
        await _reminderService.ProcessDueReminderAsync(
            grainId,
            reminderName,
            scheduleId,
            cancellationToken,
            context.DequeueCount);
    }

    private sealed class ActiveDelivery(AdvancedReminderDeliveryPreparation preparation, int durableJobDequeueCount)
    {
        public AdvancedReminderDeliveryPreparation Preparation { get; } = preparation;
        public int DurableJobDequeueCount { get; } = durableJobDequeueCount;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
