namespace Documentation.Diagnostics.DurableTasks;

public sealed class ReceiptGrain(IEmailSender email) : DurableGrain
{
#pragma warning disable ORLEANS0122
    // <orleans0122_violation>
    public async DurableTask SendReceiptAsync(string customer)
    {
        // The step completes when SendReceipt reaches its first await, before the email is sent.
        await DurableTask.Run(SendReceipt, customer).WithId("receipt");
    }

    private async void SendReceipt(string customer, CancellationToken cancellationToken) =>
        await email.SendAsync(customer, "Your receipt", cancellationToken);
    // </orleans0122_violation>
#pragma warning restore ORLEANS0122

    // <orleans0122_fix>
    public async DurableTask SendReceiptAndWaitAsync(string customer)
    {
        // The step completes when the email is sent.
        await DurableTask.Run(cancellationToken => email.SendAsync(customer, "Your receipt", cancellationToken))
            .WithId("receipt");
    }
    // </orleans0122_fix>
}
