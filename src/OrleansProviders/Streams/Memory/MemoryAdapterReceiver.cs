using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Memory
{
    internal class MemoryAdapterReceiver : IQueueAdapterReceiver
    { 
        private IMemoryStreamQueueGrain queueGrain;
        private long sequenceId;
        private List<Task> awaitingTasks;
        private Logger logger;

        public MemoryAdapterReceiver(IMemoryStreamQueueGrain queueGrain, Logger logger)
        {
            this.queueGrain = queueGrain;
            this.logger = logger;
            awaitingTasks = new List<Task>();
        }

        public Task Initialize(TimeSpan timeout)
        {
            return TaskDone.Done;
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            IEnumerable<MemoryEventData> eventData;
            List<IBatchContainer> batches;
            Task<List<MemoryEventData>> task = null;
            try
            {
                task = queueGrain.Dequeue(maxCount);
                awaitingTasks.Add(task);
                eventData = await task;
                batches = eventData.Select(data => (IBatchContainer) MemoryBatchContainer.FromMemoryEventData<object>(data, ++sequenceId)).ToList();
            }
            catch (Exception exc)
            {
                logger.Error((int)ProviderErrorCode.MemoryStreamProviderBase_GetQueueMessagesAsync, "Exception thrown in MemoryAdapterFactory.GetQueueMessagesAsync.", exc);
                throw;
            }
            finally
            {
                awaitingTasks.Remove(task);
            }
            return batches;
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            return TaskDone.Done;
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            try
            {
                if (awaitingTasks.Count != 0)
                {
                    await Task.WhenAll(awaitingTasks);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
