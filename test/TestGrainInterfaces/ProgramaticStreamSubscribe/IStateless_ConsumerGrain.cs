using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
    public interface IStateless_ConsumerGrain: IGrainWithGuidKey
    {
        Task StopConsuming();
        Task<int> GetCountOfOnAddFuncCalled();
        Task<int> GetNumberConsumed();
    }

    //the consumer grain marker interface which would unsubscribe on any subscription added by StreamSubscriptionManager
    public interface IJerk_ConsumerGrain : IGrainWithGuidKey
    {
    }

    public interface IImplicitSubscribeGrain: IGrainWithGuidKey
    {
    }

    public interface ITypedProducerGrain: IGrainWithGuidKey
    {
        Task BecomeProducer(Guid streamId, string streamNamespace, string providerToUse);

        Task StartPeriodicProducing();

        Task StopPeriodicProducing();

        Task<int> GetNumberProduced();

        Task ClearNumberProduced();
        Task Produce();
    }

    public interface ITypedProducerGrainProducingInt : ITypedProducerGrain
    { }

    public interface ITypedProducerGrainProducingString : ITypedProducerGrain
    { }
}
