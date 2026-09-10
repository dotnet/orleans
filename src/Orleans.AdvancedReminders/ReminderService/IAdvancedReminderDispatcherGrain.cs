using Orleans.Concurrency;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal interface IAdvancedReminderDispatcherGrain : IGrainWithStringKey, IDurableJobHandler
{
    Task<IGrainReminder> RegisterOrUpdateAsync(ReminderEntry entry);

    Task<IGrainReminder> ReconcileAttributeAsync(ReminderEntry entry, string declarationId);

    Task<string> UpsertAndScheduleAsync(ReminderEntry entry, CancellationToken cancellationToken);

    Task UnregisterAsync(ReminderData reminder);

    [AlwaysInterleave]
    Task ProcessDueReminderAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        int durableJobDequeueCount,
        CancellationToken cancellationToken);

    Task<AdvancedReminderDeliveryPreparation> PrepareDeliveryAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        int durableJobDequeueCount,
        Guid deliveryToken,
        CancellationToken cancellationToken);

    [AlwaysInterleave]
    Task ObserveDeliveryAsync(Guid deliveryToken, CancellationToken cancellationToken);

    Task CompleteDeliveryAsync(
        Guid deliveryToken,
        Exception? deliveryException,
        bool deliveryCancelled,
        CancellationToken cancellationToken);

    Task EnsureScheduledAsync(
        GrainId grainId,
        string reminderName,
        string? expectedScheduleId,
        bool force,
        CancellationToken cancellationToken);
}

internal enum AdvancedReminderDeliveryOutcome
{
    Skipped,
    Ready,
    Wait
}

[GenerateSerializer]
internal sealed record AdvancedReminderDeliveryPreparation
{
    [Id(0)] public AdvancedReminderDeliveryOutcome Outcome { get; init; }
    [Id(1)] public Guid DeliveryToken { get; init; }
    [Id(2)] public ReminderEntry? Entry { get; init; }
    [Id(3)] public DateTime CallbackTime { get; init; }
}
