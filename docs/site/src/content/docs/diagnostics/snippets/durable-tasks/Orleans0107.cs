namespace Documentation.Diagnostics.DurableTasks;

public sealed class RetryingGrain(IPaymentGateway payments) : DurableGrain
{
#pragma warning disable ORLEANS0107
    // <orleans0107_violation>
    public async DurableTask<Charge> ChargeAsync(decimal amount)
    {
        var key = DurableTask.RunInline("$key", () => Guid.NewGuid().ToString("N"));
        return await DurableTask.Run(ct => payments.ChargeAsync(key, amount, ct)).WithId("charge$1");
    }
    // </orleans0107_violation>
#pragma warning restore ORLEANS0107

    // <orleans0107_fix>
    public async DurableTask<Charge> ChargeOnceAsync(decimal amount)
    {
        var key = DurableTask.RunInline("key", () => Guid.NewGuid().ToString("N"));
        return await DurableTask.Run(ct => payments.ChargeAsync(key, amount, ct)).WithId("charge-1");
    }
    // </orleans0107_fix>
}
