using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace ReplicatedEventSample.Interfaces
{
    public interface IEventGrain : IGrainWithStringKey
    {
        Task NewOutcome(Outcome outcome);

        Task<List<KeyValuePair<string, int>>> GetTopThree();

    }

  
}
