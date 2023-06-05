using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class StreamingHistoryGrain : Grain, IStreamingHistoryGrain, IAsyncObserver<int>
    {
        private List<int> receivedItems = new List<int>();
        private List<StreamSubscriptionHandle<int>> subscriptionHandles = new List<StreamSubscriptionHandle<int>>();

        public async Task BecomeConsumer(StreamId streamId, string provider, string filterData = null)
        {
            var stream = this.GetStreamProvider(provider).GetStream<int>(streamId);
            subscriptionHandles.Add(await stream.SubscribeAsync(this, null, filterData));
        }

        public Task<List<int>> GetReceivedItems() => Task.FromResult(receivedItems);

        public async Task StopBeingConsumer()
        {
            foreach (var sub in subscriptionHandles)
            {
                await sub.UnsubscribeAsync();
            }
        }

        public Task OnCompletedAsync() => Task.CompletedTask;

        public Task OnErrorAsync(Exception ex) => Task.CompletedTask;

        public Task OnNextAsync(int item, StreamSequenceToken token = null)
        {
            receivedItems.Add(item);
            return Task.CompletedTask;
        }
    }
}