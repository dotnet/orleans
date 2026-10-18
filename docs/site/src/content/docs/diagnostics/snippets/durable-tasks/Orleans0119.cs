namespace Documentation.Diagnostics.DurableTasks;

public sealed class NotificationGrain(IShipmentNotifier notifier) : DurableGrain
{
#pragma warning disable ORLEANS0119
    // <orleans0119_violation>
    public async DurableTask ShipAsync(string customer)
    {
        // Starts the notification and never observes it: a failure goes unnoticed, and deleting this task's tree
        // cancels a notification which is still running.
        await notifier.NotifyShippedAsync(customer).WithId("notify").ScheduleAsync();
        await Pack().WithId("pack");
    }
    // </orleans0119_violation>
#pragma warning restore ORLEANS0119

    // <orleans0119_fix_await>
    public async DurableTask ShipWhileNotifyingAsync(string customer)
    {
        // Notifies while the order is packed, and observes the notification's outcome before the task completes.
        var notification = await notifier.NotifyShippedAsync(customer).WithId("notify").ScheduleAsync();
        await Pack().WithId("pack");
        await notification;
    }
    // </orleans0119_fix_await>

    // <orleans0119_fix_detach>
    public async DurableTask ShipThenNotifyAsync(string customer)
    {
        await Pack().WithId("pack");

        // Starts the notifier grain's durable call as an independent task, which continues after this task completes.
        _ = notifier.NotifyShippedAsync(customer).Detached().WithId($"notify-{customer}").Post();
    }
    // </orleans0119_fix_detach>

    private static async DurableTask<bool> Pack() => true;
}

// Stands in for a notifier grain's interface. Detaching applies to durable grain calls.
public interface IShipmentNotifier
{
    DurableTask NotifyShippedAsync(string customer);
}
