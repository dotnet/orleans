namespace Documentation.Diagnostics.DurableTasks;

public sealed class NotificationGrain(IEmailSender email) : DurableGrain
{
#pragma warning disable ORLEANS0119
    // <orleans0119_violation>
    public async DurableTask ShipAsync(string customer)
    {
        // Starts the notification and never observes it: a failure goes unnoticed, and deleting this task's tree
        // cancels a notification which is still running.
        await Notify(customer).WithId("notify").ScheduleAsync();
        await Pack().WithId("pack");
    }
    // </orleans0119_violation>
#pragma warning restore ORLEANS0119

    // <orleans0119_fix_await>
    public async DurableTask ShipWhileNotifyingAsync(string customer)
    {
        // Notifies while the order is packed, and observes the notification's outcome before the task completes.
        var notification = await Notify(customer).WithId("notify").ScheduleAsync();
        await Pack().WithId("pack");
        await notification;
    }
    // </orleans0119_fix_await>

    // <orleans0119_fix_detach>
    public async DurableTask ShipThenNotifyAsync(string customer)
    {
        await Pack().WithId("pack");

        // Starts the notification as an independent task, which continues after this task completes.
        _ = Notify(customer).Detached().WithId($"notify-{customer}").Post();
    }
    // </orleans0119_fix_detach>

    private static async DurableTask<bool> Pack() => true;

    private async DurableTask Notify(string customer) =>
        await email.SendAsync(customer, "Your order shipped", DurableTask.Current.CancellationToken);
}
