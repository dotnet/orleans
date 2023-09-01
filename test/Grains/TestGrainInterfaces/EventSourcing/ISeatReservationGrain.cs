namespace TestGrainInterfaces
{
    // The grain supports an operation to reserve a seat
    public interface ISeatReservationGrain : IGrainWithIntegerKey
    {
        // returns a boolean if reservation was successful
        Task<bool> Reserve(int seatnumber, string userid);
    }



}
