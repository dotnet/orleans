using System;
using System.Threading.Tasks;

using Orleans;
using Orleans.Streams;

namespace LoadTest.Streaming.GrainInterfaces
{
    public interface IStreamConsumerGrain : IGrain
    {
        Task SetVerbosity(double verbosity, long period);

        Task<int> GetReceivedCount();
        Task<int> GetErrorsCount();

        Task Ping();

        Task<StreamSubscriptionHandle<int>> BecomeConsumer(Guid streamId, string providerName);
        Task RemoveConsumer(Guid streamId, string providerName, StreamSubscriptionHandle<int> consumerHandle);
        Task ClearGrain();
    }

    public interface IFilteredStreamConsumerGrain : IStreamConsumerGrain
    {
        Task<StreamSubscriptionHandle<int>> BecomeConsumer(Guid streamId, string providerName, bool sendEvensOnly);
    }

    public interface IStreamProducerGrain : IGrain
    {
        Task SetVerbosity(double verbosity, long period);

        Task<int> GetSendCount();
        Task<int> GetErrorsCount();

        Task Ping();

        Task BecomeProducer(Guid streamId, string providerName);
        Task ClearGrain();

        Task SendItem(int item);
    }
}