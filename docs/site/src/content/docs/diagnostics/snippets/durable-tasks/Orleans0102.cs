namespace Documentation.Diagnostics.DurableTasks;

public sealed class InvoiceGrain(IExchangeRates rates, IEmailSender email) : DurableGrain
{
#pragma warning disable ORLEANS0102
    // <orleans0102_violation>
    public async DurableTask<decimal> InvoiceAsync(string customer, decimal amount, string currency)
    {
        // A replay calls the service again: it can return another rate, or fail where the first run succeeded.
        var rate = await rates.GetRateAsync(currency, DurableTask.Current.CancellationToken);
        var total = await Bill(amount * rate).WithId("bill");
        await DurableTask.Delay(TimeSpan.FromDays(30)).WithId("due");
        return total;
    }
    // </orleans0102_violation>
#pragma warning restore ORLEANS0102

    // <orleans0102_fix_run>
    public async DurableTask<decimal> InvoiceWithRecordedRateAsync(string customer, decimal amount, string currency)
    {
        // The step records the rate, so a replay returns it without calling the service.
        var rate = await DurableTask.Run(ct => rates.GetRateAsync(currency, ct)).WithId("rate");
        var total = await Bill(amount * rate).WithId("bill");
        await DurableTask.Delay(TimeSpan.FromDays(30)).WithId("due");
        return total;
    }
    // </orleans0102_fix_run>

    // <orleans0102_fix_step>
    public async DurableTask<decimal> InvoiceAndNotifyAsync(string customer, decimal amount)
    {
        var total = await Bill(amount).WithId("bill");
        await NotifyAsync(customer, total).WithId("notify");
        return total;
    }

    // A local step: a replay returns its recorded completion without sending the email again.
    private async DurableTask NotifyAsync(string customer, decimal total) =>
        await email.SendAsync(customer, $"Your invoice for {total:C}", DurableTask.Current.CancellationToken);
    // </orleans0102_fix_step>

    // <orleans0102_fix_tail>
    public async DurableTask<decimal> InvoiceThenNotifyAsync(string customer, decimal amount)
    {
        var total = await Bill(amount).WithId("bill");

        // No durable operation follows, so only a run which completes the task sends the email. A run which ends
        // before the result commits sends it again.
        await email.SendAsync(customer, $"Your invoice for {total:C}", DurableTask.Current.CancellationToken);
        return total;
    }
    // </orleans0102_fix_tail>

    private static async DurableTask<decimal> Bill(decimal amount) => decimal.Round(amount, 2);
}
