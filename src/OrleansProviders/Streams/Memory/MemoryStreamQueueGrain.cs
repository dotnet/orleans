using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OrleansProviders.Options;
using OrleansProviders.Streams.Memory;

namespace Orleans.Providers
{
    /// <summary>
    /// Memory stream queue grain. This grain works as a storage queue of event data. Enqueue and Dequeue operations are supported.
    /// the max event count sets the max storage limit to the queue.
    /// </summary>
    public class MemoryStreamQueueGrain : Grain, IMemoryStreamQueueGrain
    {
        private readonly MemoryStreamProviderHashLookup hashLookup;
        private readonly IOptionsSnapshot<MemoryStreamOptions> options;
        private readonly Queue<MemoryMessageData> eventQueue = new Queue<MemoryMessageData>();
        private long sequenceNumber = DateTime.UtcNow.Ticks;

        public MemoryStreamQueueGrain(MemoryStreamProviderHashLookup hashLookup, IOptionsSnapshot<MemoryStreamOptions> options)
        {
            this.hashLookup = hashLookup;
            this.options = options;
        }

        public override Task OnActivateAsync()
        {
            // discover the owning stream provider name from the first four bytes of the key
            var key = this.GetPrimaryKey();
            var bytes = key.ToByteArray();
            var hash = BitConverter.ToInt32(bytes, 0);
            var name = this.hashLookup.GetByHash(hash);

            // get the options for the owning stream provider name
            var options = this.options.Get(name);

            // keep what is needed
            maxEventCount = options.MaxEventCount;

            return base.OnActivateAsync();
        }

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
            return Task.CompletedTask;
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
