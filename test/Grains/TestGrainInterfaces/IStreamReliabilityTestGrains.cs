//#define USE_GENERICS

using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
#if USE_GENERICS
    public interface IStreamReliabilityTestGrain<in T> : IGrainWithIntegerKey
#else
    public interface IStreamReliabilityTestGrain : IGrainWithIntegerKey
#endif
    {
        Task<int> GetReceivedCount();
        Task<int> GetErrorsCount();
        Task<int> GetConsumerCount();

        Task Ping();
#if USE_GENERICS
        Task<StreamSubscriptionHandle<T>> AddConsumer(Guid streamId, string providerName);
        Task RemoveConsumer(Guid streamId, string providerName, StreamSubscriptionHandle<T> consumerHandle);
#else
        Task<StreamSubscriptionHandle<int>> AddConsumer(Guid streamId, string providerName);
        Task RemoveConsumer(Guid streamId, string providerName, StreamSubscriptionHandle<int> consumerHandle);
#endif
        
        Task BecomeProducer(Guid streamId, string providerName);
        Task RemoveProducer(Guid streamId, string providerName);
        Task ClearGrain();
        Task RemoveAllConsumers();

        Task<bool> IsConsumer();
        Task<bool> IsProducer();
        Task<int> GetConsumerHandlesCount();
        Task<int> GetConsumerObserversCount();

#if USE_GENERICS
        Task SendItem(T item);
#else
        Task SendItem(int item);
#endif

        Task<SiloAddress> GetLocation();
    }

        
    public interface IStreamUnsubscribeTestGrain : IGrainWithIntegerKey
    {
        Task Subscribe(Guid streamId, string providerName);
        Task UnSubscribeFromAllStreams();
    }
}