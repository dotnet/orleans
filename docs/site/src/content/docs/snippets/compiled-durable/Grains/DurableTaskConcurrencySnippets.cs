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
        catch (Exception exception) when (exception is TimeoutException || DurableTask.Current.IsCancellationRequested)
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
