using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TestGrainInterfaces;
using Orleans.Providers;
using Orleans.EventSourcing;

namespace TestGrains
{
    /// <summary>
    /// An example of a journaled grain that records seat reservations.
    /// 
    /// Configured to have one instance per cluster.
    /// Configured to use the default storage provider.
    /// Configured to use the StateStorage consistency provider.
    /// 
    /// This means we persist the latest state only ... we are not truly "event sourcing".
    /// It is not necessary here to persist all events, as the state already stores all the successful reservations.
    /// 
    /// </summary>

    [StorageProvider(ProviderName = "Default")]
    [LogConsistencyProvider(ProviderName = "StateStorage")]
    public class SeatReservationGrain : JournaledGrain<ReservationState,SeatReservation>, ISeatReservationGrain
    {
      
        public async Task<bool> Reserve(int seatnumber, string userid)
        {
            // first, enqueue the request
            RaiseEvent(new SeatReservation() { Seat = seatnumber, UserId = userid });

            // then, wait for the request to propagate
            await ConfirmEvents();

            // we can determine if the reservation went through
            // by re-reading it - if it is not there, it means a different user won
            var success = (State.Reservations.ContainsKey(seatnumber)
                                 && State.Reservations[seatnumber].UserId == userid);
            return success;
        }
    }


    /// <summary>
    /// The state of the reservation grain
    /// </summary>
    [Serializable]
    [Orleans.GenerateSerializer]
    public class ReservationState
    {
        [Orleans.Id(0)]
        public Dictionary<int, SeatReservation> Reservations { get; set; }

        public ReservationState()
        {
            Reservations = new Dictionary<int, SeatReservation>();
        }

        void Apply(SeatReservation reservation)
        {
            // see if this reservation targets an available seat
            // otherwise, treat it as a no-op
            // (this is a "first writer wins" conflict resolution)
            if (!Reservations.ContainsKey(reservation.Seat))
                Reservations.Add(reservation.Seat, reservation);
        }
    }

    /// <summary>
    /// The class that defines the update operation when a reservation is requested
    /// </summary>
    [Serializable]
    [Orleans.GenerateSerializer]
    public class SeatReservation
    {
        [Orleans.Id(0)]
        public int Seat { get; set; }
        [Orleans.Id(1)]
        public string UserId { get; set; }
    }


}

