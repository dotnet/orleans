namespace Documentation.Diagnostics.DurableTasks;

public sealed class RetryingGrain(IPaymentGateway payments) : DurableGrain
{
#pragma warning disable ORLEANS0207
    // <orleans0207_violation>
    public async DurableTask<Charge> ChargeAsync(decimal amount)
    {
        var key = $"{this.GetGrainId()}/{DurableTask.Current.Id}/charge";
        return await DurableTask.Run(ct => payments.ChargeAsync(key, amount, ct)).WithId("charge$1");
    }
    // </orleans0207_violation>
#pragma warning restore ORLEANS0207

    // <orleans0207_fix>
    public async DurableTask<Charge> ChargeOnceAsync(decimal amount)
    {
        var key = $"{this.GetGrainId()}/{DurableTask.Current.Id}/charge";
        return await DurableTask.Run(ct => payments.ChargeAsync(key, amount, ct)).WithId("charge-1");
    }
    // </orleans0207_fix>
}
