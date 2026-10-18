namespace Documentation.Diagnostics.DurableTasks;

public sealed class SettlementGrain : DurableGrain
{
    private readonly IDurableValue<OrderStatus> _status;

    public SettlementGrain() => _status = StateManager.GetOrAddValue<OrderStatus>("status");

#pragma warning disable ORLEANS0121
    // <orleans0121_violation>
    public async DurableTask MarkPaidAsync()
    {
        // A step: a child execution, which commits the change in a journal write of its own.
        await DurableTask.Run(_ => { _status.Value = OrderStatus.Paid; }).WithId("paid");
        await Ship().WithId("ship");
    }
    // </orleans0121_violation>
#pragma warning restore ORLEANS0121

    // <orleans0121_fix>
    public async DurableTask MarkPaidInlineAsync()
    {
        // An effect: the change and its record commit with the execution's next journal write.
        DurableTask.RunInline("paid", () => { _status.Value = OrderStatus.Paid; });
        await Ship().WithId("ship");
    }
    // </orleans0121_fix>

    private async DurableTask Ship() => _status.Value = OrderStatus.Shipped;
}
