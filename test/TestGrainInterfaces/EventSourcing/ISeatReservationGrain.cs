using Orleans;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestGrainInterfaces
{
    // The grain supports an operation to reserve a seat
    public interface ISeatReservationGrain : IGrainWithIntegerKey
    {
        // returns a boolean if reservation was successful
        Task<bool> Reserve(int seatnumber, string userid);
    }



}
