using Orleans.Runtime;
using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
    public interface IStreamLifecycleConsumerGrain : IGrainWithGuidKey
    {
        Task<int> GetReceivedCount();
        Task<int> GetErrorsCount();

        Task Ping();
        Task BecomeConsumer(StreamId streamId, string providerName);
        Task TestBecomeConsumerSlim(StreamId streamId, string providerName);
        Task RemoveConsumer(StreamId streamId, string providerName, StreamSubscriptionHandle<int> consumerHandle);
        Task ClearGrain();
    }

    public interface IFilteredStreamConsumerGrain : IStreamLifecycleConsumerGrain
    {
        Task BecomeConsumer(StreamId streamId, string providerName, bool sendEvensOnly);
        Task SubscribeWithBadFunc(StreamId streamId, string providerName);
    }

    public interface IStreamLifecycleProducerGrain : IGrainWithGuidKey
    {
        Task<int> GetSendCount();
        Task<int> GetErrorsCount();

        Task Ping();

        Task BecomeProducer(StreamId streamId, string providerName);
        Task ClearGrain();

        Task DoDeactivateNoClose();

        Task SendItem(int item);
    }

    public static class StreamLifecycleConsumerGrainExtensions
    {
        public static Task BecomeConsumer(this IStreamLifecycleConsumerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.BecomeConsumer(streamId, providerName);
        }

        public static Task TestBecomeConsumerSlim(this IStreamLifecycleConsumerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.TestBecomeConsumerSlim(streamId, providerName);
        }

        public static  Task RemoveConsumer(this IStreamLifecycleConsumerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName, StreamSubscriptionHandle<int> consumerHandle)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.RemoveConsumer(streamId, providerName, consumerHandle);
        }

        public static Task BecomeConsumer(this IFilteredStreamConsumerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName, bool sendEvensOnly)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.BecomeConsumer(streamId, providerName, sendEvensOnly);
        }

        public static Task SubscribeWithBadFunc(this IFilteredStreamConsumerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.SubscribeWithBadFunc(streamId, providerName);
        }

        public static Task BecomeProducer(this IStreamLifecycleProducerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.BecomeProducer(streamId, providerName);
        }
    }
}
