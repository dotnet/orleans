using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IStatelessWorkerStreamProducerGrain : IGrainWithIntegerKey
    {
        Task Produce(Guid streamId, string providerToUse, string message);
    }
}
