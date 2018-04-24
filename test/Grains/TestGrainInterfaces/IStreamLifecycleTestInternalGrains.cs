using System;
using System.Threading.Tasks;

namespace UnitTests.GrainInterfaces
{

    public interface IStreamLifecycleProducerInternalGrain : IStreamLifecycleProducerGrain
    {
        Task DoBadDeactivateNoClose();
        Task TestInternalRemoveProducer(Guid streamId, string providerName);
    }

    public interface IStreamLifecycleConsumerInternalGrain : IStreamLifecycleConsumerGrain
    {
        Task TestBecomeConsumerSlim(Guid streamId, string providerName);
    }
}