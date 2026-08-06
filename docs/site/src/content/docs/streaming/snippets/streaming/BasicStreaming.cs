using Orleans;
using Orleans.Streams;

namespace Orleans.Docs.Snippets.Streaming;

// Sample grain for demonstrating stream access
public class MyGrain : Grain
{
    // <get_stream_provider>
    public async Task SetupStream()
    {
        IStreamProvider streamProvider = this.GetStreamProvider("SimpleStreamProvider");
        StreamId streamId = StreamId.Create("MyStreamNamespace", this.GetPrimaryKey());
        IAsyncStream<string> stream = streamProvider.GetStream<string>(streamId);
    }
    // </get_stream_provider>

    // <produce_event>
    public async Task ProduceEvent(IAsyncStream<string> stream, string eventData)
    {
        await stream.OnNextAsync(eventData);
    }
    // </produce_event>

    // <subscribe_to_stream>
    public async Task<StreamSubscriptionHandle<string>> SubscribeToStream(
        IAsyncStream<string> stream, IAsyncObserver<string> observer)
    {
        StreamSubscriptionHandle<string> subscriptionHandle = await stream.SubscribeAsync(observer);
        return subscriptionHandle;
    }
    // </subscribe_to_stream>

    // <unsubscribe_from_stream>
    public async Task UnsubscribeFromStream(StreamSubscriptionHandle<string> subscriptionHandle)
    {
        await subscriptionHandle.UnsubscribeAsync();
    }
    // </unsubscribe_from_stream>

    // <get_all_subscription_handles>
    public async Task<IList<StreamSubscriptionHandle<string>>> GetSubscriptions(
        IAsyncStream<string> stream)
    {
        IList<StreamSubscriptionHandle<string>> allMyHandles =
            await stream.GetAllSubscriptionHandles();
        return allMyHandles;
    }
    // </get_all_subscription_handles>

    // <resume_subscription>
    public async Task<StreamSubscriptionHandle<int>> ResumeSubscription(
        StreamSubscriptionHandle<int> subscriptionHandle, IAsyncObserver<int> observer)
    {
        StreamSubscriptionHandle<int> newHandle =
            await subscriptionHandle.ResumeAsync(observer);
        return newHandle;
    }
    // </resume_subscription>
}
