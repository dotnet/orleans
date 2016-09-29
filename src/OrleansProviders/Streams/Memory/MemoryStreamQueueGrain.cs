using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Memory
{
    /// <summary>
    /// Memory stream queue grain. This grain works as a storage queue of event data. Enqueue and Dequeue operations are supported.
    /// the max event count sets the max storage limit to the queue.
    /// </summary>
    public class MemoryStreamQueueGrain : Grain, IMemoryStreamQueueGrain
    {
        /// <summary>
        /// Type name. Default to the grain name.
        /// </summary>
        public const String TypeName = "MemoryStreamQueueGrain";

        /// <summary>
        /// Event queue. 
        /// </summary>
        private Queue<MemoryEventData> eventQueue = new Queue<MemoryEventData>();

        /// <summary>
        /// max event count. 
        /// </summary>
        private int maxEventCount = 16384;

        /// <summary>
        /// Set the maxEventCount. This member variable sets the limit to the queue size.
        /// </summary>
        /// <param name="maxEventCount"></param>
        /// <returns></returns>
        public Task SetMaxEventCount(int maxEventCount)
        {
            if (maxEventCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEventCount), "maxEventCount must be larger than 0");
            }
            this.maxEventCount = maxEventCount;
            return TaskDone.Done;
        }

        /// <summary>
        /// Enqueues an event data. If the current total count reaches the max limit. throws an exception.
        /// </summary>
        /// <param name="eventData"></param>
        /// <returns></returns>
        public Task Enqueue(MemoryEventData eventData)
        {
            if (eventQueue.Count >= maxEventCount)
            {
                throw new InvalidOperationException($"Can not enqueue since the count has reached its maximum of {maxEventCount}");
            }
            eventQueue.Enqueue(eventData);
            return TaskDone.Done;
        }

        /// <summary>
        /// Dequeues up to a max amount of maxCount event data from the queue.
        /// </summary>
        /// <param name="maxCount"></param>
        /// <returns></returns>
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
