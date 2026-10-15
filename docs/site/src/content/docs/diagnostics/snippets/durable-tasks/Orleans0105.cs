namespace Documentation.Diagnostics.DurableTasks;

public sealed class FulfillmentGrain(IEmailSender email) : DurableGrain
{
#pragma warning disable ORLEANS0105, CS4014
    // <orleans0105_violation>
    public async DurableTask FulfillAsync(string customer)
    {
        await Pack().WithId("pack");

        // Creates a definition and discards it: the receipt never sends.
        SendReceipt(customer);
    }
    // </orleans0105_violation>
#pragma warning restore ORLEANS0105, CS4014

    // <orleans0105_fix_await>
    public async DurableTask FulfillAndSendAsync(string customer)
    {
        await Pack().WithId("pack");
        await SendReceipt(customer).WithId("receipt");
    }
    // </orleans0105_fix_await>

    // <orleans0105_fix_schedule>
    public async DurableTask FulfillWhileSendingAsync(string customer)
    {
        // Starts the receipt and continues; the execution owns it and observes its outcome before it returns.
        var receipt = await SendReceipt(customer).WithId("receipt").ScheduleAsync();
        await Pack().WithId("pack");
        await receipt;
    }
    // </orleans0105_fix_schedule>

    // <orleans0105_fix_post>
    public async DurableTask FulfillAndPostAsync(string customer)
    {
        await Pack().WithId("pack");

        // Stages the receipt with the execution's next journal write, without waiting for it to start.
        var receipt = SendReceipt(customer).WithId("receipt").Post();
        await receipt;
    }
    // </orleans0105_fix_post>

    private static async DurableTask<bool> Pack() => true;

    private async DurableTask SendReceipt(string customer) =>
        await email.SendAsync(customer, "Your receipt", DurableTask.Current.CancellationToken);
}
