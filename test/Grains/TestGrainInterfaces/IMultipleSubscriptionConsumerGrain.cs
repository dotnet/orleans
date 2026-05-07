using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
    public interface IMultipleSubscriptionConsumerGrain : IGrainWithGuidKey
    {
        Task<StreamSubscriptionHandle<int>> BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse);

        Task<StreamSubscriptionHandle<int>> Resume(StreamSubscriptionHandle<int> handle);

        Task StopConsuming(StreamSubscriptionHandle<int> handle);

        Task<IList<StreamSubscriptionHandle<int>>> GetAllSubscriptions(Guid streamId, string streamNamespace, string providerToUse);

        Task<Dictionary<StreamSubscriptionHandle<int>, Tuple<int,int>>> GetNumberConsumed();

        Task ClearNumberConsumed();

        Task Deactivate();

        Task RegisterCountObserver(Guid handleId, int expectedCount, IMultipleSubscriptionConsumerObserver observer);
    }

    public interface IMultipleSubscriptionConsumerObserver : IGrainObserver
    {
        void ConsumedCountReached(Guid handleId, int count);

        void ConsumptionFailed(Guid handleId, int errorCount);
    }
}
