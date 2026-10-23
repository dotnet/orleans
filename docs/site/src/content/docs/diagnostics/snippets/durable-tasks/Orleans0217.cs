namespace Documentation.Diagnostics.DurableTasks;

public sealed class RoomHoldGrain(IRoomInventory rooms) : DurableGrain
{
#pragma warning disable ORLEANS0217
    // <orleans0217_violation>
    public async DurableTask<OrderResult> HoldAsync(string room)
    {
        try
        {
            await Hold(room).WithId("hold");
            await DurableTask.Delay(TimeSpan.FromMinutes(15)).WithId("hold-period");
            return await Confirm(room).WithId("confirm");
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            // Task cancellation has already canceled CancellationToken, so the release ends at once.
            await rooms.ReleaseAsync(room, DurableTask.Current.CancellationToken);
            return new OrderResult(OrderStatus.Cancelled, DurableTask.Current.TaskCancellationReason);
        }
    }
    // </orleans0217_violation>
#pragma warning restore ORLEANS0217

    // <orleans0217_fix_attempt_token>
    public async DurableTask<OrderResult> HoldOrReleaseAsync(string room)
    {
        try
        {
            await Hold(room).WithId("hold");
            await DurableTask.Delay(TimeSpan.FromMinutes(15)).WithId("hold-period");
            return await Confirm(room).WithId("confirm");
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            // AttemptCancellationToken is canceled only when the host cancels the attempt, so the release runs to completion.
            await rooms.ReleaseAsync(room, DurableTask.Current.AttemptCancellationToken);
            return new OrderResult(OrderStatus.Cancelled, DurableTask.Current.TaskCancellationReason);
        }
    }
    // </orleans0217_fix_attempt_token>

    // <orleans0217_fix_shielded_step>
    public async DurableTask<OrderResult> HoldOrReleaseOnceAsync(string room)
    {
        try
        {
            await Hold(room).WithId("hold");
            await DurableTask.Delay(TimeSpan.FromMinutes(15)).WithId("hold-period");
            return await Confirm(room).WithId("confirm");
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            // The task's cancellation doesn't reach a shielded step, and the step records the release.
            await DurableTask.Run(ct => rooms.ReleaseAsync(room, ct)).WithId("release").Shielded();
            return new OrderResult(OrderStatus.Cancelled, DurableTask.Current.TaskCancellationReason);
        }
    }
    // </orleans0217_fix_shielded_step>

    private static async DurableTask<bool> Hold(string room) => room.Length > 0;

    private static async DurableTask<OrderResult> Confirm(string room) => new(OrderStatus.Reserved, room);
}
