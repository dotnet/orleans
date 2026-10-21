namespace Documentation.Diagnostics.DurableTasks;

public sealed class PaymentGrain(IPaymentGateway payments) : DurableGrain
{
    private static readonly DurableSignal<Decision> Approved = new("approved");

#pragma warning disable ORLEANS0201
    // <orleans0201_violation>
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
    // </orleans0201_violation>
#pragma warning restore ORLEANS0201

    // <orleans0201_fix>
    public async DurableTask<Charge?> PayOnceAsync(decimal amount)
    {
        // The grain, the task's identifier, and its incarnation name this run in every replay, so together they identify
        // the charge, and a later run of the same identifier charges under another key.
        var idempotencyKey = $"{this.GetGrainId()}/{DurableTask.Current.Id}/{DurableTask.Current.Incarnation:N}/charge";

        // Logical time: the runtime records it, so a replay computes the same deadline.
        var deadline = DurableTask.Current.UtcNow.AddDays(3);

        var (received, decision) = await Approved.TryWaitAsync(deadline).WithId("approval");
        if (!received || !decision.Approved)
        {
            return null;
        }

        return await DurableTask.Run(ct => payments.ChargeAsync(idempotencyKey, amount, ct)).WithId("charge");
    }
    // </orleans0201_fix>
}
