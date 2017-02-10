using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace ReplicatedEventSample.Interfaces
{
    public interface IEventGrain : IGrainWithStringKey
    {
        Task NewOutcome(Outcome outcome);

        Task<List<KeyValuePair<string, int>>> GetTopThree();
    }
}