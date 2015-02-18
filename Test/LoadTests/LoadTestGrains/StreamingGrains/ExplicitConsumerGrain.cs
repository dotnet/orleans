using System;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using Orleans.Streams;

namespace LoadTestGrains
{
    public class ExplicitConsumerGrain : ConsumerGrain<StreamingLoadTestBaseEvent>, IExplicitConsumerGrain
    {
        private string _streamProviderName;

        public async Task Subscribe(Guid streamId, string streamNamespace, StreamingLoadTestStartEvent item, StreamSequenceToken token)
        {
            _streamProviderName = item.StreamProvider;
            await SubscribeAsync(_streamProviderName, streamId, streamNamespace, token);

            // Simulate activation delay
            await item.TaskDelay();
            item.BusyWait();
        }

        protected override async Task OnNextAsync(StreamingLoadTestBaseEvent item, StreamSequenceToken token = null)
        {
            if (item is StreamingLoadTestEvent)
            {
                await item.TaskDelay();
                item.BusyWait();
            }
            else if (item is StreamingLoadTestEndEvent)
            {
                await item.TaskDelay();
                item.BusyWait();

                await base.UnsubscribeAsync();
            }

            await base.OnNextAsync(item, token);
        }
    }
}