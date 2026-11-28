namespace Orleans.AdvancedReminders.Runtime.ReminderService;

[GenerateSerializer]
internal sealed class ReminderDeliveryException(
    GrainId grainId,
    string reminderName,
    int durableJobDequeueCount,
    Exception innerException)
    : Exception(
        $"Reminder '{reminderName}' on grain '{grainId}' failed at Durable Jobs dequeue count {durableJobDequeueCount}.",
        innerException);
