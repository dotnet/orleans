namespace Documentation.Diagnostics.DurableTasks;

// <orleans0219_contract>
public interface INotificationGrain : IGrainWithStringKey
{
    DurableTask ShipThenNotifyAsync(string customer);

    DurableTask NotifyAsync(string customer);
}
// </orleans0219_contract>

public sealed class NotificationGrain(IEmailSender email) : DurableGrain, INotificationGrain
{
#pragma warning disable ORLEANS0219
    // <orleans0219_violation>
    public async DurableTask ShipAsync(string customer)
    {
        // Starts the notification and never observes it: a failure goes unnoticed, and deleting this task's tree
        // cancels a notification which is still running.
        await NotifyAsync(customer).WithId("notify").ScheduleAsync();
        await Pack().WithId("pack");
    }
    // </orleans0219_violation>
#pragma warning restore ORLEANS0219

    // <orleans0219_fix_await>
    public async DurableTask ShipWhileNotifyingAsync(string customer)
    {
        // Notifies while the order is packed, and observes the notification's outcome before the task completes.
        var notification = await NotifyAsync(customer).WithId("notify").ScheduleAsync();
        await Pack().WithId("pack");
        await notification;
    }
    // </orleans0219_fix_await>

    // <orleans0219_fix_detach>
    public async DurableTask ShipThenNotifyAsync(string customer)
    {
        await Pack().WithId("pack");

        // Calls the grain through its reference, starting an independent root which continues after this task completes.
        _ = this.AsReference<INotificationGrain>().NotifyAsync(customer).Detached().WithId($"notify-{customer}").Post();
    }
    // </orleans0219_fix_detach>

    private static async DurableTask<bool> Pack() => true;

    public async DurableTask NotifyAsync(string customer) =>
        await email.SendAsync(customer, "Your order shipped", DurableTask.Current.CancellationToken);
}
