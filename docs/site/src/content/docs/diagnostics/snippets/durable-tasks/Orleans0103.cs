namespace Documentation.Diagnostics.DurableTasks;

public sealed class ShipmentGrain : DurableGrain
{
    private readonly IEmailSender _email;
    private readonly IDurableValue<OrderStatus> _status;
    private readonly IDurableValue<int> _notices;

    public ShipmentGrain(IEmailSender email)
    {
        _email = email;
        _status = StateManager.GetOrAddValue<OrderStatus>("status");
        _notices = StateManager.GetOrAddValue<int>("notices");
    }

#pragma warning disable ORLEANS0103
    // <orleans0103_violation>
    private async DurableTask<int> NotifyShippedAsync(string customer)
    {
        // Another journal write can commit this change while the email is sending, without the step's result.
        // The step then runs again and counts the notice twice.
        _notices.Value++;
        await _email.SendAsync(customer, "Your order has shipped", DurableTask.Current.CancellationToken);
        return _notices.Value;
    }
    // </orleans0103_violation>
#pragma warning restore ORLEANS0103

    // <orleans0103_fix_reorder>
    private async DurableTask<int> NotifyShippedThenCountAsync(string customer)
    {
        await _email.SendAsync(customer, "Your order has shipped", DurableTask.Current.CancellationToken);

        // The step's final synchronous code: the change commits in the same journal write as the step's result.
        return ++_notices.Value;
    }
    // </orleans0103_fix_reorder>

    // <orleans0103_fix_split>
    public async DurableTask<int> ShipAsync(string customer)
    {
        DurableTask.RunInline("shipped", () => _status.Value = OrderStatus.Shipped);
        await DurableTask.Run(ct => _email.SendAsync(customer, "Your order has shipped", ct)).WithId("notify");
        return DurableTask.RunInline("count", () => ++_notices.Value);
    }
    // </orleans0103_fix_split>
}
