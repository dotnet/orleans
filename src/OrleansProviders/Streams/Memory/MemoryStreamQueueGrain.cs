using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Memory
{
    public class MemoryStreamQueueGrain : Grain, IMemoryStreamQueueGrain
    {
        public const String TypeName = "MemoryStreamQueueGrain";
        private Queue<MemoryEventData> eventQueue = new Queue<MemoryEventData>();
        private int maxEventCount = 16384;

        public Task SetMaxEventCount(int maxEventCount)
        {
            if (maxEventCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEventCount), "maxEventCount must be larger than 0");
            }
            this.maxEventCount = maxEventCount;
            return TaskDone.Done;
        }

        public Task Enqueue(MemoryEventData eventData)
        {
            if (eventQueue.Count >= maxEventCount)
            {
                throw new InvalidOperationException($"Can not enqueue since the count has reached its maximum of {maxEventCount}");
            }
            eventQueue.Enqueue(eventData);
            return TaskDone.Done;
        }
         
        public Task<List<MemoryEventData>> Dequeue(int maxCount)
        {
            List<MemoryEventData> list = new List<MemoryEventData>();

            for (int i = 0; i < maxCount && eventQueue.Count > 0; ++i)
            {
                list.Add(eventQueue.Dequeue());
            }

            return Task.FromResult(list);
        }
    }
}
