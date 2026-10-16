namespace Documentation.Diagnostics.DurableTasks;

public sealed class BookingGrain : DurableGrain
{
    private readonly IDurableValue<OrderStatus> _status;

    public BookingGrain() => _status = StateManager.GetOrAddValue<OrderStatus>("status");

#pragma warning disable ORLEANS0112
    // <orleans0112_violation>
    public async DurableTask<OrderResult> BookAsync(string room)
    {
        try
        {
            await Hold(room).WithId("hold");
            await DurableTask.Delay(TimeSpan.FromMinutes(15)).WithId("hold-period");
        }
        catch (OperationCanceledException)
        {
            // Also runs when the host cancels the attempt, for example when the activation stops: the handler compensates
            // for a booking which nobody canceled.
            _status.Value = OrderStatus.Cancelled;
            return new OrderResult(OrderStatus.Cancelled, DurableTask.Current.TaskCancellationReason);
        }

        return await Confirm(room).WithId("confirm");
    }
    // </orleans0112_violation>
#pragma warning restore ORLEANS0112

    // <orleans0112_fix_filter>
    public async DurableTask<OrderResult> BookOrReleaseAsync(string room)
    {
        try
        {
            await Hold(room).WithId("hold");
            await DurableTask.Delay(TimeSpan.FromMinutes(15)).WithId("hold-period");
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            // Runs for task cancellation only. When the host cancels the attempt, the exception ends it, and the next
            // attempt replays the task.
            await Release(room).WithId("release").Shielded();
            _status.Value = OrderStatus.Cancelled;
            return new OrderResult(OrderStatus.Cancelled, DurableTask.Current.TaskCancellationReason);
        }

        return await Confirm(room).WithId("confirm");
    }
    // </orleans0112_fix_filter>

    // <orleans0112_fix_rethrow>
    public async DurableTask<OrderResult> BookOrCancelAsync(string room)
    {
        try
        {
            await Hold(room).WithId("hold");
            await DurableTask.Delay(TimeSpan.FromMinutes(15)).WithId("hold-period");
        }
        catch (OperationCanceledException)
        {
            // Rethrows after compensating, so the task ends canceled, and a canceled attempt ends too.
            await Release(room).WithId("release").Shielded();
            throw;
        }

        return await Confirm(room).WithId("confirm");
    }
    // </orleans0112_fix_rethrow>

    private static async DurableTask<bool> Hold(string room) => room.Length > 0;

    private static async DurableTask<bool> Release(string room) => room.Length > 0;

    private async DurableTask<OrderResult> Confirm(string room)
    {
        _status.Value = OrderStatus.Paid;
        return new(OrderStatus.Paid, room);
    }
}
