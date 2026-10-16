using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableTasks;
using Orleans.Journaling;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.DurableTasks;

// <durable_tasks_trip_contracts>
[GenerateSerializer, Immutable]
public sealed record TripRequest(string Flight, string Hotel, string Traveler);

[GenerateSerializer, Immutable]
public sealed record TripResult(bool Booked, string? Seat, string? Room, string? Reason);

public interface IFlightGrain : IGrainWithStringKey
{
    DurableTask<string> HoldSeatAsync(Guid tripId);

    DurableTask<bool> ReleaseSeatAsync(Guid tripId);
}

public interface IHotelGrain : IGrainWithStringKey
{
    DurableTask<string> HoldRoomAsync(Guid tripId);

    DurableTask<bool> ReleaseRoomAsync(Guid tripId);
}

public interface IItineraryGrain : IGrainWithStringKey
{
    DurableTask<bool> SendItineraryAsync(Guid tripId, string seat, string room);
}

public interface ITripGrain : IGrainWithGuidKey
{
    DurableTask<TripResult> BookAsync(TripRequest request);
}
// </durable_tasks_trip_contracts>

// <durable_tasks_trip_grain>
public sealed class TripGrain : DurableGrain, ITripGrain
{
    private Guid TripId => this.GetPrimaryKey();

    public async DurableTask<TripResult> BookAsync(TripRequest request)
    {
        var flight = GrainFactory.GetGrain<IFlightGrain>(request.Flight);
        var hotel = GrainFactory.GetGrain<IHotelGrain>(request.Hotel);
        string[] holds;
        try
        {
            // Attached children: the trip owns both holds, its cancellation reaches them, and WhenAll returns only once
            // both are terminal. The whole fan-out gives up after ten minutes, and its inputs are canceled and awaited.
            holds = await DurableTask.WhenAll([flight.HoldSeatAsync(TripId), hotel.HoldRoomAsync(TripId)])
                .WaitAsync(TimeSpan.FromMinutes(10))
                .WithId("hold");
        }
        catch (Exception exception) when (exception is TimeoutException || DurableTask.Current.IsTaskCancellationRequested)
        {
            // Shielded children run to completion although the trip's cancellation was requested, and the trip still
            // owns and awaits them. Releasing a hold which was never taken returns false.
            await DurableTask.WhenAll([flight.ReleaseSeatAsync(TripId), hotel.ReleaseRoomAsync(TripId)])
                .WithId("release")
                .Shielded();
            DurableTask.Current.CancellationToken.ThrowIfCancellationRequested();
            return new TripResult(false, null, null, "The holds weren't confirmed in time.");
        }

        // A detached task is an independent root: it outlives the trip, and the trip's cancellation doesn't reach it.
        _ = GrainFactory.GetGrain<IItineraryGrain>(request.Traveler)
            .SendItineraryAsync(TripId, holds[0], holds[1])
            .Detached()
            .WithId($"itinerary-{TripId:N}")
            .Post();
        return new TripResult(true, holds[0], holds[1], null);
    }
}
// </durable_tasks_trip_grain>

[GenerateSerializer, Immutable]
public sealed record TripPayment(string ChargeId, decimal Amount);

public interface ITripPaymentsGrain : IGrainWithStringKey
{
    DurableTask<bool> RefundAsync(TripPayment payment);
}

public interface ITripCheckoutGrain : IGrainWithGuidKey
{
    DurableTask<TripPayment> AwaitPaymentAsync();
}

public sealed class TripCheckoutGrain : DurableGrain, ITripCheckoutGrain
{
    private static readonly DurableSignal<TripPayment> PaymentReceived = new("payment-received");

    public async DurableTask<TripPayment> AwaitPaymentAsync()
    {
        var payments = GrainFactory.GetGrain<ITripPaymentsGrain>("payments");
        // <durable_tasks_shielded_wait>
        var payment = await PaymentReceived.WaitAsync().Shielded();
        if (DurableTask.Current.IsTaskCancellationRequested)
        {
            await payments.RefundAsync(payment).WithId("refund").Shielded();
        }
        // </durable_tasks_shielded_wait>
        DurableTask.Current.CancellationToken.ThrowIfCancellationRequested();
        return payment;
    }
}

public interface IShippingLabelGrain : IGrainWithStringKey
{
    DurableTask<string> ShipAsync(string orderId);
}

// <durable_tasks_attempt_cancellation>
public sealed class ShippingLabelGrain(
    HttpClient carrier,
    [FromKeyedServices("label")] IDurableValue<string> label) : DurableGrain, IShippingLabelGrain
{
    private readonly SemaphoreSlim _carrierRequests = new(4);

    public async DurableTask<string> ShipAsync(string orderId)
    {
        // The step records the label, so a replay doesn't buy another one.
        label.Value = await DurableTask.Run(cancellationToken => BuyLabelAsync(orderId, cancellationToken)).WithId("label");
        await DurableTask.Delay(TimeSpan.FromHours(4)).WithId("pickup-window");
        return label.Value;
    }

    private async Task<string> BuyLabelAsync(string orderId, CancellationToken cancellationToken)
    {
        // Ordinary calls receive the task's CancellationToken, which task and attempt cancellation both cancel.
        await _carrierRequests.WaitAsync(cancellationToken);
        try
        {
            using var response = await carrier.PostAsync($"labels/{orderId}", content: null, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            // Durable cleanup runs for task cancellation. Its calls receive the attempt's token, because the task's
            // token is already canceled, and they end promptly if the host cancels the attempt during the cleanup.
            using var voided = await carrier.DeleteAsync($"labels/{orderId}", DurableTask.Current.AttemptCancellationToken);
            throw;
        }
        finally
        {
            // In-process cleanup runs for both kinds of cancellation.
            _carrierRequests.Release();
        }
    }
}
// </durable_tasks_attempt_cancellation>
