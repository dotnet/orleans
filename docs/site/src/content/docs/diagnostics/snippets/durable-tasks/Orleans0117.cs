namespace Documentation.Diagnostics.DurableTasks;

public interface IReservationService
{
    Task ReserveAsync(string reservationId, CancellationToken cancellationToken);

    Task ReleaseAsync(string reservationId, CancellationToken cancellationToken);
}

public sealed class ReservationGrain(IReservationService reservations) : DurableGrain
{
#pragma warning disable ORLEANS0117
    // <orleans0117_violation>
    public async DurableTask<bool> HoldAsync(string reservationId)
    {
        try
        {
            await DurableTask.Run(cancellationToken => reservations.ReserveAsync(reservationId, cancellationToken)).WithId("reserve");
            await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("hold-period");
            return true;
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            // Task cancellation has already canceled CancellationToken, so the release ends before it reaches the service.
            await reservations.ReleaseAsync(reservationId, DurableTask.Current.CancellationToken);
            return false;
        }
    }
    // </orleans0117_violation>
#pragma warning restore ORLEANS0117

    // <orleans0117_fix>
    public async DurableTask<bool> HoldOrReleaseAsync(string reservationId)
    {
        try
        {
            await DurableTask.Run(cancellationToken => reservations.ReserveAsync(reservationId, cancellationToken)).WithId("reserve");
            await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("hold-period");
            return true;
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            // Runs to completion, and ends promptly if the host cancels the attempt, for example when the activation stops.
            await reservations.ReleaseAsync(reservationId, DurableTask.Current.AttemptCancellationToken);
            return false;
        }
    }
    // </orleans0117_fix>
}
