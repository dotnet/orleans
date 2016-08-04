using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Memory
{
    public class MemoryStreamQueueGrain : Grain, IMemoryStreamQueueGrain
    {
        public const String TypeName = "MemoryStreamQueueGrain";
        private Queue<MemoryEventData> eventQueue;
        private int maxEventCount = 16384;

        public override Task OnActivateAsync()
        {
            eventQueue = new Queue<MemoryEventData>();
            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            eventQueue = null;
            return TaskDone.Done;
        }

        public Task SetMaxEventCount(int maxEventCount)
        {
            this.maxEventCount = maxEventCount;
            return TaskDone.Done;
        }

        public Task Enqueue(MemoryEventData eventData)
        {
            if (eventQueue.Count >= maxEventCount)
            {
                throw new Exception("MemoryStreamQueueGrain.Enqueue: Count has reached maxEventCount ("+ maxEventCount+")");
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
