
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers
{
    internal class MemoryAdapterReceiver<TSerializer> : IQueueAdapterReceiver
        where TSerializer : IMemoryMessageBodySerializer, new()
    { 
        private readonly IMemoryStreamQueueGrain queueGrain;
        private readonly List<Task> awaitingTasks;
        private readonly Logger logger;

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
            List<IBatchContainer> batches;
            Task<List<MemoryMessageData>> task = null;
            try
            {
                task = queueGrain.Dequeue(maxCount);
                awaitingTasks.Add(task);
                IEnumerable<MemoryMessageData> eventData = await task;
                batches = eventData.Select(data => new MemoryBatchContainer<TSerializer>(data)).ToList<IBatchContainer>();
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
