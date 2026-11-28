namespace Orleans.AdvancedReminders.Runtime.ReminderService;

// A provider released its waiter, but its submitted write can still commit.
internal sealed class ReminderTableMutationCanceledException(Task pendingMutation, CancellationToken cancellationToken)
    : OperationCanceledException(cancellationToken)
{
    public Task PendingMutation { get; } = pendingMutation;
}
