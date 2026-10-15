namespace Documentation.Diagnostics.DurableTasks;

public sealed class AuditGrain(IEmailSender email) : DurableGrain
{
#pragma warning disable ORLEANS0113
    // <orleans0113_violation>
    public async DurableTask CloseAccountAsync(string customer)
    {
        await Close(customer).WithId("close");

        // The body returns before the email is sent, and a failure to send escapes the body.
        NotifyClosed(customer);

        async void NotifyClosed(string recipient) =>
            await email.SendAsync(recipient, "Your account is closed", DurableTask.Current.CancellationToken);
    }
    // </orleans0113_violation>
#pragma warning restore ORLEANS0113

    // <orleans0113_fix_step>
    public async DurableTask CloseAccountAndNotifyAsync(string customer)
    {
        await Close(customer).WithId("close");
        await NotifyClosed(customer).WithId("notify");

        // A step which the body awaits: the body observes its completion and its failure, and a replay returns its record.
        async DurableTask NotifyClosed(string recipient) =>
            await email.SendAsync(recipient, "Your account is closed", DurableTask.Current.CancellationToken);
    }
    // </orleans0113_fix_step>

    // <orleans0113_fix_func>
    public async DurableTask CloseAccountWithCallbackAsync(string customer, Func<string, Task> onClosed)
    {
        await Close(customer).WithId("close");

        // A delegate which returns a task, run as a step which the body awaits.
        await DurableTask.Run(_ => onClosed(customer)).WithId("on-closed");
    }
    // </orleans0113_fix_func>

    private static async DurableTask<bool> Close(string customer) => customer.Length > 0;
}
