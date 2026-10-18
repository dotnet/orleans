namespace Documentation.Diagnostics.DurableTasks;

public sealed class PaymentGrain(IPaymentGateway payments) : DurableGrain
{
    private static readonly DurableSignal<Decision> Approved = new("approved");

#pragma warning disable ORLEANS0101
    // <orleans0101_violation>
    public async DurableTask<Charge?> PayAsync(decimal amount)
    {
        // A replay draws another key and computes another deadline.
        var idempotencyKey = Guid.NewGuid().ToString("N");
        var deadline = DateTimeOffset.UtcNow.AddDays(3);

        var (received, decision) = await Approved.TryWaitAsync(deadline).WithId("approval");
        if (!received || !decision.Approved)
        {
            return null;
        }

        return await DurableTask.Run(ct => payments.ChargeAsync(idempotencyKey, amount, ct)).WithId("charge");
    }
    // </orleans0101_violation>
#pragma warning restore ORLEANS0101

    // <orleans0101_fix>
    public async DurableTask<Charge?> PayOnceAsync(decimal amount)
    {
        // The effect draws the key once, and every replay returns the recorded key.
        var idempotencyKey = DurableTask.RunInline("idempotency-key", () => Guid.NewGuid().ToString("N"));

        // Logical time: the runtime records it, so a replay computes the same deadline.
        var deadline = DurableTask.Current.UtcNow.AddDays(3);

        var (received, decision) = await Approved.TryWaitAsync(deadline).WithId("approval");
        if (!received || !decision.Approved)
        {
            return null;
        }

        return await DurableTask.Run(ct => payments.ChargeAsync(idempotencyKey, amount, ct)).WithId("charge");
    }
    // </orleans0101_fix>

    // <orleans0101_fix_task_id>
    public async DurableTask<Charge> PayWithTaskIdAsync(decimal amount)
    {
        // The grain and the task's identifier name this execution in every replay, so together they identify the charge.
        var idempotencyKey = $"{this.GetGrainId()}/{DurableTask.Current.Id}/charge";
        return await DurableTask.Run(ct => payments.ChargeAsync(idempotencyKey, amount, ct)).WithId("charge");
    }
    // </orleans0101_fix_task_id>
}
