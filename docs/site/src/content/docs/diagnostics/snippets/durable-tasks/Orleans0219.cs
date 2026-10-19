namespace Documentation.Diagnostics.DurableTasks;

public sealed class DispatchGrain(IEmailSender email) : DurableGrain
{
#pragma warning disable ORLEANS0219
    // <orleans0219_violation>
    public async DurableTask DispatchAsync(string customer)
    {
        // Starts the notice and discards its handle: the notice's failure reaches no code.
        await Notify(customer).WithId("notify").ScheduleAsync();
        await Pack().WithId("pack");
    }
    // </orleans0219_violation>
#pragma warning restore ORLEANS0219

    // <orleans0219_fix_await>
    public async DurableTask DispatchAndNotifyAsync(string customer)
    {
        // The notice runs while the body packs, and the body observes its outcome before it completes.
        var notice = await Notify(customer).WithId("notify").ScheduleAsync();
        await Pack().WithId("pack");
        await notice;
    }
    // </orleans0219_fix_await>

    // <orleans0219_fix_combinator>
    public async DurableTask DispatchAndInvoiceAsync(string customer)
    {
        var notice = await Notify(customer).WithId("notify").ScheduleAsync();
        var invoice = await Invoice(customer).WithId("invoice").ScheduleAsync();
        await Pack().WithId("pack");
        await ScheduledTask.WhenAll([notice, invoice]);
    }
    // </orleans0219_fix_combinator>

    // <orleans0219_fix_detached>
    public async DurableTask DispatchAndSurveyAsync(string customer)
    {
        await Pack().WithId("pack");

        // An independent root task, which continues after this execution completes and is deleted.
        await Survey(customer).WithId("survey").Detached().ScheduleAsync();
    }
    // </orleans0219_fix_detached>

    private static async DurableTask<bool> Pack() => true;

    private async DurableTask Notify(string customer) =>
        await email.SendAsync(customer, "Your order is on its way", DurableTask.Current.CancellationToken);

    private async DurableTask Invoice(string customer) =>
        await email.SendAsync(customer, "Your invoice", DurableTask.Current.CancellationToken);

    private async DurableTask Survey(string customer) =>
        await email.SendAsync(customer, "How was your order?", DurableTask.Current.CancellationToken);
}
