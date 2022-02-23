using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IStatelessWorkerStreamConsumerGrain : IGrainWithIntegerKey
    {
        Task BecomeConsumer(Guid streamId, string providerToUse);
    }
}
