
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Providers
{
    /// <summary>
    /// Memory stream queue grain. This grain works as a storage queue of event data. Enqueue and Dequeue operations are supported.
    /// the max event count sets the max storage limit to the queue.
    /// </summary>
    public class MemoryStreamQueueGrain : Grain, IMemoryStreamQueueGrain
    {
        private readonly Queue<MemoryMessageData> eventQueue = new Queue<MemoryMessageData>();
        private long sequenceNumber = DateTime.UtcNow.Ticks;

        /// <summary>
        /// max event count. 
        /// </summary>
        private int maxEventCount = 16384;

        /// <summary>
        /// Enqueues an event data. If the current total count reaches the max limit. throws an exception.
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public Task Enqueue(MemoryMessageData data)
        {
            if (eventQueue.Count >= maxEventCount)
            {
                throw new InvalidOperationException($"Can not enqueue since the count has reached its maximum of {maxEventCount}");
            }
            data.SequenceNumber = sequenceNumber++;
            eventQueue.Enqueue(data);
            return TaskDone.Done;
        }

        /// <summary>
        /// Dequeues up to a max amount of maxCount event data from the queue.
        /// </summary>
        /// <param name="maxCount"></param>
        /// <returns></returns>
        public Task<List<MemoryMessageData>> Dequeue(int maxCount)
        {
            List<MemoryMessageData> list = new List<MemoryMessageData>();

            for (int i = 0; i < maxCount && eventQueue.Count > 0; ++i)
            {
                list.Add(eventQueue.Dequeue());
            }

            return Task.FromResult(list);
        }
    }
}
