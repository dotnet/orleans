using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// Consumer grain which passively reacts to subscriptions which was made on behalf of
    /// it using Programmatic Subscribing 
    /// </summary>
    public interface IPassive_ConsumerGrain: IGrainWithGuidKey, IStreamSubscriptionObserver
    {
        Task StopConsuming();
        Task<int> GetCountOfOnAddFuncCalled();
        Task<int> GetNumberConsumed();
    }

    //the consumer grain marker interface which would unsubscribe on any subscription added by StreamSubscriptionManager
    public interface IJerk_ConsumerGrain : IGrainWithGuidKey, IStreamSubscriptionObserver
    {
    }

    public interface IImplicitSubscribeGrain: IGrainWithGuidKey
    {
    }

    public interface ITypedProducerGrain: IGrainWithGuidKey
    {
        Task BecomeProducer(Guid streamId, string streamNamespace, string providerToUse);

        Task StartPeriodicProducing(TimeSpan? firePeriod = null);

        Task StopPeriodicProducing();

        Task<int> GetNumberProduced();

        Task ClearNumberProduced();
        Task Produce();
    }

    public interface ITypedProducerGrainProducingInt : ITypedProducerGrain
    { }

    public interface ITypedProducerGrainProducingApple : ITypedProducerGrain
    { }

    public interface IFruit
    {
        int GetNumber();
    }
}
