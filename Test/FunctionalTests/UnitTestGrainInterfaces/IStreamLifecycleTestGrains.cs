using System;
using System.Threading.Tasks;

using Orleans;
using Orleans.Streams;

namespace UnitTestGrainInterfaces
{
    public interface IStreamLifecycleConsumerGrain : IGrain
    {
        Task<int> GetReceivedCount();
        Task<int> GetErrorsCount();

        Task Ping();
        Task BecomeConsumer(Guid streamId, string providerName);
        Task TestBecomeConsumerSlim(Guid streamId, string providerName);
        Task RemoveConsumer(Guid streamId, string providerName, StreamSubscriptionHandle<int> consumerHandle);
        Task ClearGrain();
    }

    public interface IFilteredStreamConsumerGrain : IStreamLifecycleConsumerGrain
    {
        Task BecomeConsumer(Guid streamId, string providerName, bool sendEvensOnly);
        Task SubscribeWithBadFunc(Guid streamId, string providerName);
    }

    public interface IStreamLifecycleProducerGrain : IGrain
    {
        Task<int> GetSendCount();
        Task<int> GetErrorsCount();

        Task Ping();

        Task BecomeProducer(Guid streamId, string providerName);
        Task TestInternalRemoveProducer(Guid streamId, string providerName);
        Task ClearGrain();

        Task DoDeactivateNoClose();
        Task DoBadDeactivateNoClose();

        Task SendItem(int item);
    }
}