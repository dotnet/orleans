namespace Documentation.Diagnostics.DurableTasks;

#pragma warning disable ORLEANS0116
// <orleans0116_violation>
public sealed class CheckoutEndpoint(IOrderWorkflows orders)
{
    // A client has no durable outbox, so Post throws InvalidOperationException.
    public void Checkout(Guid orderId) =>
        orders.PlaceOrderAsync(orderId).WithId($"place-order-{orderId:N}").Post();
}
// </orleans0116_violation>
#pragma warning restore ORLEANS0116

// <orleans0116_fix_schedule>
public sealed class ScheduledCheckoutEndpoint(IOrderWorkflows orders)
{
    // Returns once the order's grain has accepted and stored the task. A retry with the same ID reaches the same task.
    public async Task<string> CheckoutAsync(Guid orderId)
    {
        var workflow = await orders.PlaceOrderAsync(orderId).WithId($"place-order-{orderId:N}").ScheduleAsync();
        return workflow.Id.ToString();
    }
}
// </orleans0116_fix_schedule>

// <orleans0116_fix_grain>
public sealed class BasketGrain : DurableGrain
{
    private readonly IOrderWorkflows _orders;
    private readonly IDurableValue<Guid?> _checkedOut;

    public BasketGrain(IOrderWorkflows orders)
    {
        _orders = orders;
        _checkedOut = StateManager.GetOrAddValue<Guid?>("checked-out");
    }

    // In a durable grain, Post stages the start in the grain's outbox, and it commits with the grain's state change.
    public async Task CheckoutAsync(Guid orderId)
    {
        _checkedOut.Value = orderId;
        _ = _orders.PlaceOrderAsync(orderId).WithId($"place-order-{orderId:N}").Post();
        await WriteStateAsync();
    }
}
// </orleans0116_fix_grain>
