using Orleans;

namespace GrainInterfaces;

public interface IProducerGrain : IGrainWithStringKey
{
    Task StartProducing(string ns, Guid key);

    Task StopProducing();
}
