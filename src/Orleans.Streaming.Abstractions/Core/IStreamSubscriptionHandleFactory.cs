using Orleans.Runtime;

namespace Orleans.Streams.Core
{
    public interface IStreamSubscriptionHandleFactory
    {
        StreamId StreamId { get; }
        string ProviderName { get; }
        GuidId SubscriptionId { get; }
        StreamSubscriptionHandle<T> Create<T>();
    }
}
